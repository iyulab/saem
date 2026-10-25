using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Saem.Console;

/// <summary>
/// Keeps a browser session's tokens current (saem `c838-01`): when the access token the session keeps is about to
/// expire, it is renewed with the session's refresh token and the session carries the new ones; when it cannot be
/// renewed — no refresh token, or the issuer refuses it — the session ends and the browser signs in again.
/// <para>
/// Two requests of one session may arrive together, both carrying the old tokens. An issuer that rotates refresh tokens
/// takes a second use of the old one as a stolen token, so one renewal is made and shared: a renewal is kept, by the
/// refresh token it spent, for <see cref="Shared"/>. That holds within one console process; several behind one address
/// share sessions only as far as their cookies' keys, not their renewals.
/// </para>
/// </summary>
internal sealed class SessionTokens(ConsoleIssuer issuer, TimeProvider time)
{
    /// <summary>How long before the access token expires it is renewed.</summary>
    internal static readonly TimeSpan Ahead = TimeSpan.FromMinutes(1);

    /// <summary>How long a renewal is kept for another request that spent the same refresh token.</summary>
    internal static readonly TimeSpan Shared = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, Task<Renewed?> Renewal)> _renewals = new(StringComparer.Ordinal);

    /// <summary>The cookie's validation: renew what is about to expire, or end the session.</summary>
    public async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var properties = context.Properties;
        if (properties.GetTokenValue("expires_at") is not { } at
            || !DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expires)
            || expires - time.GetUtcNow() > Ahead)
        {
            return;
        }

        var renewed = properties.GetTokenValue("refresh_token") is { Length: > 0 } refresh
            ? await RenewAsync(refresh).ConfigureAwait(false)
            : null;
        if (renewed is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            return;
        }

        properties.UpdateTokenValue("access_token", renewed.AccessToken);
        properties.UpdateTokenValue("expires_at", renewed.ExpiresAt.ToString("o", CultureInfo.InvariantCulture));
        if (renewed.RefreshToken is { Length: > 0 } rotated)
        {
            properties.UpdateTokenValue("refresh_token", rotated);
        }

        context.ShouldRenew = true;
    }

    // Not tied to the request that started it: another request may be waiting on the same renewal, and a renewal the
    // issuer completed but nobody kept would leave the session holding a refresh token the issuer has retired.
    private Task<Renewed?> RenewAsync(string refreshToken)
    {
        var now = time.GetUtcNow();
        foreach (var stale in _renewals.Where(r => now - r.Value.At > Shared).Select(r => r.Key).ToList())
        {
            _renewals.TryRemove(stale, out _);
        }

        // Keyed by the token's hash: the dictionary holds no refresh token itself.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
        return _renewals.GetOrAdd(key, _ => (now, RequestAsync(refreshToken, CancellationToken.None))).Renewal;
    }

    private async Task<Renewed?> RequestAsync(string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            var issued = await issuer.TokenAsync(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, cancellationToken).ConfigureAwait(false);
            return new Renewed(issued.AccessToken, issued.RefreshToken, time.GetUtcNow() + (issued.Lifetime ?? Ahead));
        }
        catch (IssuerRefusedException)
        {
            return null;
        }
    }

    private sealed record Renewed(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);
}
