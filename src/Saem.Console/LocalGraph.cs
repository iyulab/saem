using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace Saem.Console;

/// <summary>
/// How the console asks the MCP server as the person looking (saem D-89 ①): the server's address, the audience its
/// tokens must name, and whether the console names itself the actor. The exchange goes to the issuer's token endpoint
/// (<see cref="ConsoleIssuer"/>).
/// </summary>
public sealed class ConsoleLookupOptions
{
    /// <summary>The MCP server's Streamable HTTP endpoint.</summary>
    public string McpEndpoint { get; set; } = "";

    /// <summary>The audience the MCP server accepts; the person's token is exchanged for one naming it.</summary>
    public string McpAudience { get; set; } = "saem";

    /// <summary>
    /// Whether the console presents itself as the actor in the exchange (RFC 8693 delegation: its own client-credentials
    /// token as <c>actor_token</c>), so the token the MCP server receives names the console in <c>act</c> and the
    /// server's record of the lookup says the person asked through the console. Off only for an issuer that refuses
    /// actor tokens — the lookup is then recorded as the person's own.
    /// </summary>
    public bool PresentActor { get; set; } = true;
}

/// <summary>Runs one lookup as the person whose console token is presented, and gives back the answer as the contract has it.</summary>
public interface IConsoleLookups
{
    /// <summary>The MCP server's answer to <paramref name="arguments"/> — the lookup tool's — asked as the token's person.</summary>
    Task<JsonElement> LookupAsync(string presentedToken, JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>
/// A lookup through the MCP server: the console's token is exchanged for one meant for the server (RFC 8693) — the
/// console's own token is never sent on, so the server never accepts a token not meant for it (ADR-0016) — with the
/// console as the actor, and the server's one tool is called with it. The console reads no source itself: what the person sees is what the server
/// answers that person, partition reports and refusals included.
/// </summary>
internal sealed class McpConsoleLookups(ConsoleIssuer issuer, IOptions<ConsoleLookupOptions> options, Func<Uri, string, JsonElement, CancellationToken, Task<JsonElement>>? call = null)
    : IConsoleLookups
{
    private const string GrantType = "urn:ietf:params:oauth:grant-type:token-exchange";
    private const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";

    private readonly Func<Uri, string, JsonElement, CancellationToken, Task<JsonElement>> _call = call ?? CallToolAsync;

    public async Task<JsonElement> LookupAsync(string presentedToken, JsonElement arguments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presentedToken);
        var settings = options.Value;
        string exchanged;
        try
        {
            var actor = settings.PresentActor
                ? (await issuer.TokenAsync(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }, cancellationToken).ConfigureAwait(false)).AccessToken
                : null;
            exchanged = (await issuer.TokenAsync(Exchange(settings, presentedToken, actor), cancellationToken).ConfigureAwait(false)).AccessToken;
        }
        catch (IssuerRefusedException e)
        {
            throw new LookupUnavailableException(e.Message, e);
        }

        return await _call(new Uri(settings.McpEndpoint), exchanged, arguments, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, string> Exchange(ConsoleLookupOptions settings, string presentedToken, string? actor)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = GrantType,
            ["subject_token"] = presentedToken,
            ["subject_token_type"] = AccessTokenType,
            ["requested_token_type"] = AccessTokenType,
            ["audience"] = settings.McpAudience,
        };
        if (actor is not null)
        {
            form["actor_token"] = actor;
            form["actor_token_type"] = AccessTokenType;
        }

        return form;
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
