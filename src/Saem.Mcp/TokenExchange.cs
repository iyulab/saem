using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Saem.Mcp;

/// <summary>Where saem exchanges a person's token, and as which client (RFC 8693).</summary>
public sealed class TokenExchangeOptions
{
    /// <summary>The issuer's token endpoint.</summary>
    public string TokenEndpoint { get; set; } = "";

    /// <summary>saem's client id at the issuer.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>saem's client secret at the issuer, sent as HTTP Basic authentication.</summary>
    public string ClientSecret { get; set; } = "";
}

/// <summary>
/// Exchanges the token a request presented for one meant for a source (OAuth 2.0 Token Exchange, RFC
/// 8693): the issuer keeps the person as the subject and names the source as the audience, so the
/// source checks the token itself and reads as that person. saem never passes the token it was called
/// with through to a source — a source accepts only a token meant for it.
/// </summary>
public sealed class TokenExchange
{
    private const string GrantType = "urn:ietf:params:oauth:grant-type:token-exchange";
    private const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";

    private readonly HttpClient _http;
    private readonly TokenExchangeOptions _options;

    public TokenExchange(HttpClient http, TokenExchangeOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.TokenEndpoint) || string.IsNullOrWhiteSpace(options.ClientId))
        {
            throw new InvalidOperationException("Saem:Sources:TokenExchange names the issuer's token endpoint and saem's client id.");
        }

        _http = http;
        _options = options;
    }

    /// <summary>Returns a token for <paramref name="audience"/>, exchanged from <paramref name="presented"/>.</summary>
    /// <exception cref="TokenExchangeException">The issuer refused the exchange.</exception>
    public async ValueTask<string> ForAsync(PresentedToken presented, string audience, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(presented);
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = GrantType,
                ["subject_token"] = presented.Value,
                ["subject_token_type"] = AccessTokenType,
                ["requested_token_type"] = AccessTokenType,
                ["audience"] = audience,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(_options.ClientId)}:{Uri.EscapeDataString(_options.ClientSecret)}")));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // The issuer's error body names the reason (invalid_grant, unauthorized_client …) and no secret.
            var reason = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new TokenExchangeException($"The issuer refused to exchange the token for '{audience}' ({(int)response.StatusCode}): {Truncate(reason)}");
        }

        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return body.RootElement.TryGetProperty("access_token", out var token) && token.GetString() is { Length: > 0 } value
            ? value
            : throw new TokenExchangeException($"The issuer's exchange for '{audience}' returned no access token.");
    }

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "…";
}

/// <summary>The issuer would not exchange a person's token for a source.</summary>
public sealed class TokenExchangeException : Exception
{
    public TokenExchangeException()
    {
    }

    public TokenExchangeException(string message)
        : base(message)
    {
    }

    public TokenExchangeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
