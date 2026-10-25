using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace Saem.Console;

/// <summary>
/// How the console asks the MCP server as the person looking (saem D-89 ①): the server's address, the audience its
/// tokens must name, and the issuer's token-exchange endpoint with the console's own client credentials.
/// </summary>
public sealed class ConsoleLookupOptions
{
    /// <summary>The MCP server's Streamable HTTP endpoint.</summary>
    public string McpEndpoint { get; set; } = "";

    /// <summary>The audience the MCP server accepts; the person's token is exchanged for one naming it.</summary>
    public string McpAudience { get; set; } = "saem";

    /// <summary>The issuer's token endpoint (RFC 8693 token exchange).</summary>
    public string TokenEndpoint { get; set; } = "";

    /// <summary>The console's client id at the issuer.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>The console's client secret at the issuer.</summary>
    public string ClientSecret { get; set; } = "";
}

/// <summary>Runs one lookup as the person whose console token is presented, and gives back the answer as the contract has it.</summary>
public interface IConsoleLookups
{
    /// <summary>The MCP server's answer to <paramref name="arguments"/> — the lookup tool's — asked as the token's person.</summary>
    Task<JsonElement> LookupAsync(string presentedToken, JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>
/// A lookup through the MCP server: the console's token is exchanged for one meant for the server (RFC 8693) — the
/// console's own token is never sent on, so the server never accepts a token not meant for it (ADR-0016) — and the
/// server's one tool is called with it. The console reads no source itself: what the person sees is what the server
/// answers that person, partition reports and refusals included.
/// </summary>
internal sealed class McpConsoleLookups(HttpClient http, IOptions<ConsoleLookupOptions> options, Func<Uri, string, JsonElement, CancellationToken, Task<JsonElement>>? call = null)
    : IConsoleLookups
{
    private const string GrantType = "urn:ietf:params:oauth:grant-type:token-exchange";
    private const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";

    private readonly Func<Uri, string, JsonElement, CancellationToken, Task<JsonElement>> _call = call ?? CallToolAsync;

    public async Task<JsonElement> LookupAsync(string presentedToken, JsonElement arguments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presentedToken);
        var settings = options.Value;
        var exchanged = await ExchangeAsync(settings, presentedToken, cancellationToken).ConfigureAwait(false);
        return await _call(new Uri(settings.McpEndpoint), exchanged, arguments, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ExchangeAsync(ConsoleLookupOptions settings, string presentedToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = GrantType,
                ["subject_token"] = presentedToken,
                ["subject_token_type"] = AccessTokenType,
                ["requested_token_type"] = AccessTokenType,
                ["audience"] = settings.McpAudience,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(settings.ClientId)}:{Uri.EscapeDataString(settings.ClientSecret)}")));

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // The issuer's error names the reason (invalid_grant, unauthorized_client …) and no secret.
            throw new LookupUnavailableException($"The issuer refused to exchange the token for the MCP server ({(int)response.StatusCode}): {body}");
        }

        using var token = JsonDocument.Parse(body);
        return token.RootElement.TryGetProperty("access_token", out var value) && value.GetString() is { Length: > 0 } exchanged
            ? exchanged
            : throw new LookupUnavailableException("The issuer's exchange answered without an access token.");
    }

    private static async Task<JsonElement> CallToolAsync(Uri endpoint, string token, JsonElement call, CancellationToken cancellationToken)
    {
        await using var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
            }),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var arguments = call.Deserialize<Dictionary<string, object?>>() ?? [];
        var result = await client.CallToolAsync("lookup", arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.StructuredContent is { } structured
            ? structured
            : throw new LookupUnavailableException(result.IsError == true ? "The MCP server could not answer the lookup." : "The MCP server answered without the contract's content.");
    }
}

/// <summary>The lookup could not be asked — the exchange was refused, or the MCP server gave no answer.</summary>
public sealed class LookupUnavailableException : Exception
{
    public LookupUnavailableException()
    {
    }

    public LookupUnavailableException(string message)
        : base(message)
    {
    }

    public LookupUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
