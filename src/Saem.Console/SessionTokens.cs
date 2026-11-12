using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
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

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, Task<Outcome> Renewal)> _renewals = new(StringComparer.Ordinal);

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

        var subject = context.Principal?.FindFirst("sub")?.Value ?? "";
        var outcome = properties.GetTokenValue("refresh_token") is { Length: > 0 } refresh
            ? await RenewAsync(refresh, subject).ConfigureAwait(false)
            : Outcome.Refused;
        if (outcome.NoAnswer)
        {
            // The issuer gave no answer, so nothing says the session is over: while the token it holds still lives the session
            // goes on, and the renewal is tried again after <see cref="Shared"/>. A token that has expired cannot be used, so
            // the request is turned away — the cookie stays, and the next request renews once the issuer answers.
            if (expires <= time.GetUtcNow())
            {
                context.RejectPrincipal();
            }

            return;
        }

        if (outcome.Tokens is not { } renewed)
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

        // The roles follow the groups the issuer names now: a renewal that names them replaces what the session carried since
        // sign-in, so a person taken out of a group loses its role here and not when the cookie ends.
        if (renewed.Groups is { } groups && context.Principal?.Identity is ClaimsIdentity carried)
        {
            var identity = new ClaimsIdentity(
                carried.Claims.Where(c => c.Type != "groups").Concat(groups.Select(g => new Claim("groups", g))),
                carried.AuthenticationType,
                carried.NameClaimType,
                carried.RoleClaimType);
            context.ReplacePrincipal(new ClaimsPrincipal(identity));
        }

        context.ShouldRenew = true;
    }

    // Not tied to the request that started it: another request may be waiting on the same renewal, and a renewal the
    // issuer completed but nobody kept would leave the session holding a refresh token the issuer has retired.
    private Task<Outcome> RenewAsync(string refreshToken, string subject)
    {
        var now = time.GetUtcNow();
        foreach (var stale in _renewals.Where(r => now - r.Value.At > Shared).Select(r => r.Key).ToList())
        {
            _renewals.TryRemove(stale, out _);
        }

        // Keyed by the token's hash: the dictionary holds no refresh token itself.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
        return _renewals.GetOrAdd(key, _ => (now, RequestAsync(refreshToken, subject, CancellationToken.None))).Renewal;
    }

    private async Task<Outcome> RequestAsync(string refreshToken, string subject, CancellationToken cancellationToken)
    {
        try
        {
            var issued = await issuer.TokenAsync(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, cancellationToken).ConfigureAwait(false);
            var groups = issued.IdToken is { Length: > 0 } idToken ? await issuer.GroupsOfAsync(idToken, subject, cancellationToken).ConfigureAwait(false) : null;
            return new Outcome(new Renewed(issued.AccessToken, issued.RefreshToken, time.GetUtcNow() + (issued.Lifetime ?? Ahead), groups), NoAnswer: false);
        }
        catch (IssuerRefusedException)
        {
            return Outcome.Refused;
        }
        catch (IssuerUnavailableException)
        {
            return Outcome.Unanswered;
        }
    }

    private sealed record Renewed(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, IReadOnlyList<string>? Groups);

    /// <summary>What a renewal came to: new tokens, a refusal (no tokens), or no answer from the issuer (kept, like a renewal, for <see cref="Shared"/>).</summary>
    private sealed record Outcome(Renewed? Tokens, bool NoAnswer)
    {
        internal static readonly Outcome Refused = new(null, NoAnswer: false);

        internal static readonly Outcome Unanswered = new(null, NoAnswer: true);
    }
}
