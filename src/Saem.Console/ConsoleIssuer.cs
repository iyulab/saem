using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace Saem.Console;

/// <summary>
/// The console as a client of its issuer's token endpoint — the one endpoint every token request goes to (sign-in's code
/// is redeemed by the OpenID Connect handler itself): renewing a session, the console's own client-credentials token,
/// and the exchange for the MCP server (RFC 8693 uses the same endpoint). The endpoint is read from the issuer's
/// discovery document, and the console authenticates as the confidential client <see cref="ConsoleIdentityOptions"/> names.
/// </summary>
internal sealed class ConsoleIssuer(IHttpClientFactory clients, IOptionsMonitor<OpenIdConnectOptions> oidc, IOptions<ConsoleIdentityOptions> identity)
{
    /// <summary>The token the endpoint issued for <paramref name="form"/>.</summary>
    /// <exception cref="IssuerRefusedException">The issuer refused the request, or answered without an access token.</exception>
    public async Task<IssuedToken> TokenAsync(IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken)
    {
        var configuration = await oidc.Get(OpenIdConnectDefaults.AuthenticationScheme).ConfigurationManager!.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var client = identity.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, configuration.TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(client.ClientId)}:{Uri.EscapeDataString(client.ClientSecret)}")));

        using var http = clients.CreateClient(nameof(ConsoleIssuer));
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var grant = form.TryGetValue("grant_type", out var g) ? g : "token";
        if (!response.IsSuccessStatusCode)
        {
            // The issuer's error names the reason (invalid_grant, unauthorized_client …) and no secret.
            throw new IssuerRefusedException($"The issuer refused the console's {grant} request ({(int)response.StatusCode}): {body}");
        }

        using var document = JsonDocument.Parse(body);
        var token = document.RootElement;
        return token.TryGetProperty("access_token", out var access) && access.GetString() is { Length: > 0 } accessToken
            ? new IssuedToken(
                accessToken,
                token.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
                token.TryGetProperty("expires_in", out var seconds) && seconds.TryGetInt32(out var s) ? TimeSpan.FromSeconds(s) : null)
            : throw new IssuerRefusedException($"The issuer answered the console's {grant} request without an access token.");
    }
}

/// <summary>What the token endpoint issued.</summary>
/// <param name="AccessToken">The access token.</param>
/// <param name="RefreshToken">A refresh token, when the issuer gave one.</param>
/// <param name="Lifetime">How long the access token lasts, when the issuer said.</param>
internal sealed record IssuedToken(string AccessToken, string? RefreshToken, TimeSpan? Lifetime);

/// <summary>The issuer refused a token request, or answered it without a token.</summary>
public sealed class IssuerRefusedException : Exception
{
    public IssuerRefusedException()
    {
    }

    public IssuerRefusedException(string message)
        : base(message)
    {
    }

    public IssuerRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
