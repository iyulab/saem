using System.Net;
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
    /// <exception cref="IssuerUnavailableException">The issuer gave no answer: it could not be reached, or it failed (a 5xx, or asking for less).</exception>
    public async Task<IssuedToken> TokenAsync(IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken)
    {
        var grant = form.TryGetValue("grant_type", out var g) ? g : "token";
        HttpStatusCode status;
        string body;
        try
        {
            var configuration = await oidc.Get(OpenIdConnectDefaults.AuthenticationScheme).ConfigurationManager!.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            using var request = AsClient(configuration.TokenEndpoint, form);

            using var http = clients.CreateClient(nameof(ConsoleIssuer));
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode;
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Not an answer: the discovery document or the token endpoint could not be reached in time. What the transport said
            // names where the issuer was looked for, so it stays with the inner exception and out of the message a browser may see.
            throw new IssuerUnavailableException($"The issuer gave no answer to the console's {grant} request: it could not be reached in time.", e);
        }

        if (status is >= HttpStatusCode.InternalServerError or HttpStatusCode.TooManyRequests)
        {
            throw new IssuerUnavailableException($"The issuer could not answer the console's {grant} request ({(int)status}).");
        }

        if ((int)status is < 200 or > 299)
        {
            // The issuer's error names the reason (invalid_grant, unauthorized_client …) and no secret.
            throw new IssuerRefusedException($"The issuer refused the console's {grant} request ({(int)status}): {body}");
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

    /// <summary>
    /// Asks the issuer to revoke a refresh token (RFC 7009), as signing out does: the session cookie is stateless, so a copy of
    /// it keeps working until its tokens end — and renews them for as long as the issuer honours the refresh token. Best effort:
    /// an issuer that names no revocation endpoint is not asked, and one that cannot be reached or does not accept the request
    /// is reported, never thrown — signing out goes on either way.
    /// </summary>
    public async Task<RevocationOutcome> RevokeAsync(string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            var configuration = await oidc.Get(OpenIdConnectDefaults.AuthenticationScheme).ConfigurationManager!.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            if (!configuration.AdditionalData.TryGetValue("revocation_endpoint", out var named) || named?.ToString() is not { Length: > 0 } endpoint)
            {
                return RevocationOutcome.NotOffered;
            }

            using var request = AsClient(endpoint, new Dictionary<string, string> { ["token"] = refreshToken, ["token_type_hint"] = "refresh_token" });
            using var http = clients.CreateClient(nameof(ConsoleIssuer));
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? RevocationOutcome.Revoked : RevocationOutcome.Failed;
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException)
        {
            return RevocationOutcome.Failed;
        }
    }

    /// <summary>A form post to <paramref name="endpoint"/> as the console's confidential client.</summary>
    private HttpRequestMessage AsClient(string endpoint, IReadOnlyDictionary<string, string> form)
    {
        var client = identity.Value;
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(client.ClientId)}:{Uri.EscapeDataString(client.ClientSecret)}")));
        return request;
    }
}

/// <summary>What asking the issuer to revoke a token came to.</summary>
internal enum RevocationOutcome
{
    /// <summary>The issuer accepted the revocation.</summary>
    Revoked,

    /// <summary>The issuer names no revocation endpoint: nothing was asked.</summary>
    NotOffered,

    /// <summary>The issuer could not be reached or did not accept the request: the token may still be honoured.</summary>
    Failed,
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

/// <summary>The issuer gave no answer — it could not be reached, or it failed. That is not a «no»: nothing was refused.</summary>
public sealed class IssuerUnavailableException : Exception
{
    public IssuerUnavailableException()
    {
    }

    public IssuerUnavailableException(string message)
        : base(message)
    {
    }

    public IssuerUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
