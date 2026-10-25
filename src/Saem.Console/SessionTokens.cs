using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

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
internal sealed class SessionTokens(IHttpClientFactory clients, IOptionsMonitor<OpenIdConnectOptions> oidc, IOptions<ConsoleIdentityOptions> identity, TimeProvider time)
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
        var options = oidc.Get(OpenIdConnectDefaults.AuthenticationScheme);
        var configuration = await options.ConfigurationManager!.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var client = identity.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, configuration.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(client.ClientId)}:{Uri.EscapeDataString(client.ClientSecret)}")));
        using var http = clients.CreateClient(nameof(SessionTokens));
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var token = body.RootElement;
        if (!token.TryGetProperty("access_token", out var access) || access.GetString() is not { Length: > 0 } accessToken)
        {
            return null;
        }

        var lifetime = token.TryGetProperty("expires_in", out var seconds) && seconds.TryGetInt32(out var s) ? TimeSpan.FromSeconds(s) : Ahead;
        return new Renewed(
            accessToken,
            token.TryGetProperty("refresh_token", out var rotated) ? rotated.GetString() : null,
            time.GetUtcNow() + lifetime);
    }

    private sealed record Renewed(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);
}
