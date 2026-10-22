using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Saem.Contracts;
using Saem.Ontology;
using Saem.Query;

namespace Saem.Mcp;

/// <summary>
/// The one tool: a lookup in the ontology's terms, run as the person the request's token
/// names. Its description and input schema are built from the ontology on every listing, so the
/// vocabulary a caller sees is the vocabulary a call is checked against. The answer travels in the
/// contract's own JSON (<see cref="ContractJson"/>), as structured content and as text.
/// </summary>
internal sealed partial class LookupTool
{
    internal const string Name = "lookup";

    private readonly IOntologyCatalog _catalog;
    private readonly LookupWalker _walker;
    private readonly ISourceLogins _logins;
    private readonly ILogger<LookupTool> _logger;

    public LookupTool(IOntologyCatalog catalog, ISourceLogins logins, ILogger<LookupTool> logger)
    {
        _catalog = catalog;
        _walker = new LookupWalker(catalog);
        _logins = logins;
        _logger = logger;
    }

    public async ValueTask<ListToolsResult> ListAsync(CancellationToken cancellationToken)
    {
        var ontology = await _catalog.GetAsync(cancellationToken).ConfigureAwait(false);
        return new ListToolsResult
        {
            Tools =
            [
                new Tool
                {
                    Name = Name,
                    Title = "Look up facts as the person this request acts for",
                    Description = Describe(ontology),
                    InputSchema = InputSchema(ontology),
                    Annotations = new ToolAnnotations { ReadOnlyHint = true, IdempotentHint = true, OpenWorldHint = false },
                },
            ],
        };
    }

    public async ValueTask<CallToolResult> CallAsync(CallToolRequestParams request, Person? person, PresentedToken? token, CancellationToken cancellationToken)
    {
        if (request.Name != Name)
        {
            return Error($"There is no tool '{request.Name}'; the one tool is '{Name}'.");
        }

        if (person is null)
        {
            // Authentication already demands a token; one that names nobody cannot be run as anyone.
            return Error("The request's token names no person (no 'sub' claim), so there is no one to run the lookup as.");
        }

        if (token is null)
        {
            // Unreachable behind authentication; refused rather than assumed, since a source's token is exchanged from it.
            return Error("The request presented no bearer token, so no source can be read as its person.");
        }

        LookupCall? call;
        try
        {
            call = JsonSerializer.Deserialize(JsonSerializer.SerializeToElement(request.Arguments ?? new Dictionary<string, JsonElement>()), ContractJson.Default.LookupCall);
        }
        catch (JsonException e)
        {
            return Error($"The arguments are not a lookup: {e.Message}");
        }

        if (call?.Start is null || call.Path is null || call.Read is null)
        {
            return Error("A lookup names 'start' (type and key), 'path' (relations, possibly none) and 'read' (fields).");
        }

        // The request's trace id when there is one (W3C trace context), so this log line, the source's log
        // and any tracing backend all name the lookup the same way.
        var lookupId = Activity.Current?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N");
        LogLookup(lookupId, person.Subject, person.Actor ?? "-", call.Start.Type, call.Path.Count, call.Read.Count);
        LookupAnswer answer;
        try
        {
            answer = await _walker.RunAsync(call, _logins.SessionsFor(person, token, lookupId), cancellationToken).ConfigureAwait(false);
        }
        catch (UnknownTermException e)
        {
            // The question is malformed, which is not a refusal: a refusal is an answer about the sources.
            return Error(e.Message);
        }
        catch (NoSourceLoginException e)
        {
            return Error(e.Message);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A source that fails is a fault, not a refusal. Its detail (hosts, server messages) stays in the log.
            LogLookupFailed(e, lookupId, person.Subject);
            return Error("A source failed while the lookup ran, so it was not answered. This is a failure, not a refusal: nothing is known about what the source holds.");
        }

        var json = JsonSerializer.SerializeToElement(answer, ContractJson.Default.LookupAnswer);
        return new CallToolResult
        {
            StructuredContent = json,
            Content = [new TextContentBlock { Text = json.GetRawText() }],
        };
    }

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };

    private static string Describe(Ontology.Ontology ontology)
    {
        var text = new StringBuilder();
        text.Append("Reads facts from the organisation's systems as the person this request acts for: ")
            .Append("start at one entity, follow declared relations in order, and read declared fields of the entity the walk ends at. ")
            .Append("Each source decides what the person may see. The answer is either facts, each with the source record it was read from and the relations followed, ")
            .Append("or a refusal saying where the walk found nothing visible to this person — which never means the thing does not exist.");
        text.Append("\n\nEntity types (fields):");
        foreach (var type in ontology.EntityTypes.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            text.Append("\n- ").Append(type.Name).Append(" (").Append(string.Join(", ", type.Fields.Keys.Order(StringComparer.Ordinal))).Append(')');
        }

        if (ontology.Relations.Count > 0)
        {
            text.Append("\n\nRelations:");
            foreach (var relation in ontology.Relations.Values.OrderBy(r => r.Name, StringComparer.Ordinal))
            {
                text.Append("\n- ").Append(relation.Name).Append(": ").Append(relation.From).Append(" -> ").Append(relation.To);
            }
        }

        return text.ToString();
    }

    private static JsonElement InputSchema(Ontology.Ontology ontology)
    {
        static JsonArray Names(IEnumerable<string> names) => [.. names.Order(StringComparer.Ordinal).Select(n => (JsonNode)n)];

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["start"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "The entity the walk starts from: its declared type and its key in its source system.",
                    ["properties"] = new JsonObject
                    {
                        ["type"] = new JsonObject { ["type"] = "string", ["enum"] = Names(ontology.EntityTypes.Keys) },
                        ["key"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("type", "key"),
                    ["additionalProperties"] = false,
                },
                ["path"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "The relations to follow, in order; empty to read the start entity itself.",
                    ["items"] = new JsonObject { ["type"] = "string", ["enum"] = Names(ontology.Relations.Keys) },
                },
                ["read"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "The fields to read on the entity the walk ends at.",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["minItems"] = 1,
                },
            },
            ["required"] = new JsonArray("start", "path", "read"),
            ["additionalProperties"] = false,
        };
        return JsonSerializer.SerializeToElement(schema, McpJson.Default.JsonObject);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "lookup {LookupId} for {Person} failed at a source")]
    private partial void LogLookupFailed(Exception exception, string lookupId, string person);

    [LoggerMessage(Level = LogLevel.Information, Message = "lookup {LookupId} for {Person} by {Actor}: start {StartType}, {Hops} hop(s), {Fields} field(s)")]
    private partial void LogLookup(string lookupId, string person, string actor, string startType, int hops, int fields);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonObject))]
internal sealed partial class McpJson : System.Text.Json.Serialization.JsonSerializerContext;
