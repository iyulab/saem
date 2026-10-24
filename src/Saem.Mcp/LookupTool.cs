using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Saem.Contracts;
using Saem.Ontology;
using Saem.Query;

namespace Saem.Mcp;

/// <summary>
/// The one tool: a lookup in the ontology's terms, run as the person the request's token
/// names. Its description and input schema are built from the ontology on every listing, so the
/// vocabulary a caller sees is the vocabulary a call is checked against — and the listing names the
/// ontology's version in its <c>_meta</c>, which every answer carries too. The answer travels in the
/// contract's own JSON (<see cref="ContractJson"/>), as structured content and as text, and its shape is
/// declared as the tool's output schema (<see cref="ContractSchema"/>).
/// </summary>
internal sealed partial class LookupTool
{
    internal const string Name = "lookup";

    /// <summary>The <c>_meta</c> key the listing names the ontology's version under.</summary>
    internal const string CatalogVersionMetaKey = "saem/catalogVersion";

    private readonly IOntologyCatalog _catalog;
    private readonly LookupWalker _walker;
    private readonly TimeSpan _deadline;
    private readonly ISourceLogins _logins;
    private readonly IGroupStores _groups;
    private readonly InteractionLog _interactions;
    private readonly ILogger<LookupTool> _logger;

    public LookupTool(IOntologyCatalog catalog, ISourceLogins logins, IGroupStores groups, InteractionLog interactions, IOptions<LookupOptions> options, ILogger<LookupTool> logger)
    {
        _interactions = interactions;
        _catalog = catalog;
        _walker = new LookupWalker(catalog, maxCandidatesPerStep: options.Value.MaxCandidatesPerStep);
        _deadline = options.Value.Deadline;
        _logins = logins;
        _groups = groups;
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
                    OutputSchema = ContractSchema.Answer,
                    Annotations = new ToolAnnotations { ReadOnlyHint = true, IdempotentHint = true, OpenWorldHint = false },
                    Meta = new JsonObject { [CatalogVersionMetaKey] = ontology.Version },
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
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            // ArgumentException: a shape the contract refuses while it is read (a start with both a key and a field, or neither).
            return Error($"The arguments are not a lookup: {e.Message}");
        }

        if (call?.Start is null || call.Path is null || call.Read is null)
        {
            return Error("A lookup names 'start' (type, and key or where), 'path' (relations, possibly none) and 'read' (fields).");
        }

        // The call's span, under the request's trace when there is one (W3C trace context): its trace id names
        // the lookup, so the interaction log, the source's log and any tracing backend all name it the same way.
        using var span = InteractionLog.Source.StartActivity("execute_tool " + Name);
        span?.SetTag("gen_ai.operation.name", "execute_tool");
        span?.SetTag("gen_ai.tool.name", Name);
        var lookupId = Activity.Current?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N");
        var started = Stopwatch.GetTimestamp();
        var at = DateTimeOffset.UtcNow;
        var ontology = await _catalog.GetAsync(cancellationToken).ConfigureAwait(false);
        string SystemOf(string type) => ontology.EntityTypes.TryGetValue(type, out var declared) ? declared.System : "-";

        void Record(string outcome, IReadOnlyList<AccessedRecord> accessed, InteractionRefusal? refusal = null, string? errorType = null, int suspect = 0) =>
            _interactions.Write(new Interaction(
                lookupId, at, Stopwatch.GetElapsedTime(started).TotalMilliseconds, person.Subject, person.Actor,
                call.Start.Type, call.Path, call.Read, outcome, accessed, refusal, errorType, _interactions.KeyId, suspect), span);

        // A start named by a field's value names no record: the value is not a key, and no value is logged (D-83).
        AccessedRecord Start(string status) =>
            new(SystemOf(call.Start.Type), call.Start.Type, call.Start.Key is { } key ? _interactions.Name(SystemOf(call.Start.Type), call.Start.Type, key) : null, status);

        LookupAnswer answer;
        // The deadline bounds the whole lookup — token exchanges, source reads, partition reads (saem D-84).
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_deadline);
        try
        {
            var relations = new GroupRelations(_groups.StoresOf(person), _groups.Iris, _logger);
            answer = await _walker.RunAsync(call, _logins.SessionsFor(person, token, lookupId), relations, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Not a refusal and not a source's fault: saem stopped waiting, so nothing is said.
            Record("failed", [], errorType: "deadline");
            return Error($"The lookup was not answered within {_deadline.TotalSeconds:0.#} seconds, so it was stopped. This is a failure, not a refusal: nothing is known about what the sources hold.");
        }
        catch (TooBroadException e)
        {
            // saem's own limit, not an answer about the sources: no count, and the caller can narrow the lookup.
            Record("invalid", [], errorType: "too_broad");
            return Error(e.Message);
        }
        catch (UnknownTermException e)
        {
            // The question is malformed, which is not a refusal: a refusal is an answer about the sources.
            Record("invalid", [], errorType: "unknown_term");
            return Error(e.Message);
        }
        catch (NoSourceLoginException e)
        {
            Record("invalid", [], errorType: "no_source_login");
            return Error(e.Message);
        }
        catch (MaterializedRelationsException e)
        {
            // The group's store failed, not a source: said as such, with its detail kept in the log.
            LogLookupFailed(e, lookupId, person.Subject);
            Record("failed", [], errorType: "materialized_relations");
            return Error($"The materialized relations of the group '{e.Partition}' could not be read, so the lookup was not answered. This is a failure, not a refusal: nothing is known about what they hold.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A source that fails is a fault, not a refusal. Its detail (hosts, server messages) stays in the log.
            LogLookupFailed(e, lookupId, person.Subject);
            Record("failed", [], errorType: "source");
            return Error("A source failed while the lookup ran, so it was not answered. This is a failure, not a refusal: nothing is known about what the source holds.");
        }

        // Values that read as instructions to a model are marked, not changed: the value is the source's fact.
        var suspect = InjectionSignals.For(answer);
        if (answer is Refused { Refusal: var refusal })
        {
            Record("refused", [Start(refusal.AtHop == 0 ? "notVisible" : "read")],
                new InteractionRefusal(JsonNamingPolicy.CamelCase.ConvertName(refusal.Reason.ToString()), refusal.AtHop, refusal.Relation, refusal.System));
        }
        else
        {
            Record(answer is Candidates ? "candidates" : "answered", _interactions.Reached(answer, SystemOf), suspect: suspect?.Count ?? 0);
        }

        // The answer is this person's view: said in it, so it is not taken for anyone else's once passed on.
        answer = answer with { AnsweredFor = new Viewer(person.Subject, person.Actor) };
        var json = JsonSerializer.SerializeToElement(answer, ContractJson.Default.LookupAnswer);
        return new CallToolResult
        {
            StructuredContent = json,
            Content = [new TextContentBlock { Text = json.GetRawText() }],
            Meta = suspect is null ? null : new JsonObject { [InjectionSignals.MetaKey] = suspect },
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
            .Append("or a refusal saying where the walk found nothing visible to this person — which never means the thing does not exist. ")
            .Append("A start named by a field's value that several visible entities hold answers with those candidates instead; start again from one by its key.");
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

        if (ontology.Inverses.Count > 0)
        {
            text.Append("\n\nRelations walked backwards (last step of a path only; each leads to every entity that names the one reached, ")
                .Append("found among the relations built ahead of time for the groups the person is in, then read as the person):");
            foreach (var (inverse, relation) in ontology.Inverses.OrderBy(i => i.Key, StringComparer.Ordinal))
            {
                text.Append("\n- ").Append(inverse).Append(": ").Append(relation.To).Append(" -> ").Append(relation.From)
                    .Append(" (").Append(relation.Name).Append(" backwards)");
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
                    ["description"] = "The entity the walk starts from: its declared type, and either its key in its source system or a declared field and the value it holds. "
                        + "A value several visible entities hold answers with those candidates to start again from by key.",
                    ["properties"] = new JsonObject
                    {
                        ["type"] = new JsonObject { ["type"] = "string", ["enum"] = Names(ontology.EntityTypes.Keys) },
                        ["key"] = new JsonObject { ["type"] = "string" },
                        ["where"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["field"] = new JsonObject { ["type"] = "string" },
                                ["value"] = new JsonObject { ["type"] = "string" },
                            },
                            ["required"] = new JsonArray("field", "value"),
                            ["additionalProperties"] = false,
                        },
                    },
                    ["required"] = new JsonArray("type"),
                    ["oneOf"] = new JsonArray(
                        new JsonObject { ["required"] = new JsonArray("key") },
                        new JsonObject { ["required"] = new JsonArray("where") }),
                    ["additionalProperties"] = false,
                },
                ["path"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "The relations to follow, in order; empty to read the start entity itself. A relation walked backwards may only be the last.",
                    ["items"] = new JsonObject { ["type"] = "string", ["enum"] = Names(ontology.Relations.Keys.Concat(ontology.Inverses.Keys)) },
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

}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonObject))]
internal sealed partial class McpJson : System.Text.Json.Serialization.JsonSerializerContext;
