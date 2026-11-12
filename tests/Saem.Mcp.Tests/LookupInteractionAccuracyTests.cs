using System.Diagnostics;
using System.Text.Json;
using Saem.Query;
using Xunit;

namespace Saem.Mcp.Tests;

/// <summary>
/// What the interaction log says about a call must be what happened: it names only what the ontology declares (a caller's
/// own words are not the log's), lists as read only what a source returned, keeps what a failing walk had already read,
/// and gives every call an id of its own whatever trace the caller says it belongs to.
/// </summary>
public sealed class LookupInteractionAccuracyTests
{
    private const string OntologyJson = """
        [
          { "kind": "entityType", "name": "WorkOrder", "system": "mes", "table": "work_orders", "key": "wo_no",
            "fields": { "note": "note", "item": "item_code" } },
          { "kind": "entityType", "name": "Item", "system": "mes", "table": "items", "key": "item_code",
            "fields": { "name": "name" } },
          { "kind": "relation", "name": "producesItem", "from": "WorkOrder", "via": "item", "to": "Item", "inverse": "producedBy" }
        ]
        """;

    private static readonly Dictionary<string, string?> Audit = new()
    {
        ["Saem:Audit:HmacKey"] = "a-deployment-secret-for-naming-records",
        ["Saem:Audit:Ocsf"] = "true",
    };

    private static FakeLogins Alice() => new(new()
    {
        ["alice"] = new()
        {
            ["work_orders"] = [FakeLogins.Row("WO-3", ("note", "n"), ("item_code", "I-200"))],
            ["items"] = [FakeLogins.Row("I-200", ("name", "Bracket"))],
        },
    });

    private static Dictionary<string, object?> Call(string type, string key, string[] path, string[] read) => new()
    {
        ["start"] = new Dictionary<string, object?> { ["type"] = type, ["key"] = key },
        ["path"] = path,
        ["read"] = read,
    };

    private static IReadOnlyList<string> Interactions(TestServer server) =>
        [.. server.Logs.Lines.Where(l => l.StartsWith("interaction ", StringComparison.Ordinal))];

    private static JsonElement Interaction(string line) => JsonDocument.Parse(line[(line.IndexOf('{', StringComparison.Ordinal))..]).RootElement.Clone();

    /// <summary>The caller's words are not the log's: a type, relation or field the ontology does not declare is not written anywhere.</summary>
    [Fact]
    public async Task A_call_naming_nothing_the_ontology_declares_logs_none_of_what_it_named()
    {
        var ct = TestContext.Current.CancellationToken;
        var tags = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Saem.Mcp",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => tags.AddRange(activity.Tags.Select(t => $"{t.Key}={t.Value}")),
        };
        ActivitySource.AddActivityListener(listener);
        await using var server = await TestServer.StartAsync(OntologyJson, Alice(), Audit);
        await using var alice = await server.ClientAsync(TestServer.Token("alice"), ct);

        var result = await alice.CallToolAsync("lookup", Call("CANARY-TYPE-7f3", "WO-3", ["CANARY-REL-7f3"], ["CANARY-FIELD-7f3"]), cancellationToken: ct);

        Assert.True(result.IsError);
        var everything = string.Join('\n', server.Logs.Lines.Concat(tags));
        Assert.Contains("unknown_term", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("CANARY", everything, StringComparison.Ordinal);
    }

    /// <summary>A walk whose first step is backwards reads nothing before it asks the partitions: its start is named, not read.</summary>
    [Fact]
    public async Task A_walk_that_read_nothing_lists_no_record_as_read()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await TestServer.StartAsync(OntologyJson, Alice(), Audit);
        await using var alice = await server.ClientAsync(TestServer.Token("alice"), ct);

        await alice.CallToolAsync("lookup", Call("Item", "I-200", ["producedBy"], ["note"]), cancellationToken: ct);

        var interaction = Interaction(Assert.Single(Interactions(server)));
        Assert.Equal("refused", interaction.GetProperty("outcome").GetString());
        Assert.Equal(0, interaction.GetProperty("accessed").GetArrayLength());
    }

    /// <summary>A source that fails halfway leaves the record it had returned in the log, and the one it failed on named as failed.</summary>
    [Fact]
    public async Task A_walk_that_fails_keeps_what_it_had_read_and_names_where_it_failed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await TestServer.StartAsync(OntologyJson, new FailingOn("items"), Audit);
        await using var alice = await server.ClientAsync(TestServer.Token("alice"), ct);

        var result = await alice.CallToolAsync("lookup", Call("WorkOrder", "WO-3", ["producesItem"], ["name"]), cancellationToken: ct);

        Assert.True(result.IsError);
        var interaction = Interaction(Assert.Single(Interactions(server)));
        Assert.Equal("failed", interaction.GetProperty("outcome").GetString());
        var accessed = interaction.GetProperty("accessed").EnumerateArray().Select(a => (a.GetProperty("type").GetString(), a.GetProperty("status").GetString())).ToList();
        Assert.Equal([("WorkOrder", "read"), ("Item", "failed")], accessed);
    }

    /// <summary>A source that answers a key with two rows is a fault; its message names the key to nobody who reads the log.</summary>
    [Fact]
    public async Task A_key_that_two_rows_answer_is_not_written_to_the_log()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await TestServer.StartAsync(OntologyJson, new FailingOn(failure: null, twoRowsFor: "work_orders"), Audit);
        await using var alice = await server.ClientAsync(TestServer.Token("alice"), ct);

        var result = await alice.CallToolAsync("lookup", Call("WorkOrder", "WO-3", [], ["note"]), cancellationToken: ct);

        Assert.True(result.IsError);
        Assert.DoesNotContain("WO-3", string.Join('\n', server.Logs.Lines), StringComparison.Ordinal);
    }

    /// <summary>The trace is the caller's to choose: two calls under one trace are still two interactions, each with its own id.</summary>
    [Fact]
    public async Task Two_calls_under_one_trace_are_two_interactions_with_their_own_ids()
    {
        var ct = TestContext.Current.CancellationToken;
        const string Traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        await using var server = await TestServer.StartAsync(OntologyJson, Alice(), Audit);
        await using var alice = await server.ClientAsync(TestServer.Token("alice"), ct, new Dictionary<string, string> { ["traceparent"] = Traceparent });

        await alice.CallToolAsync("lookup", Call("WorkOrder", "WO-3", [], ["note"]), cancellationToken: ct);
        await alice.CallToolAsync("lookup", Call("WorkOrder", "WO-3", [], ["note"]), cancellationToken: ct);

        var interactions = Interactions(server).Select(Interaction).ToList();
        Assert.Equal(2, interactions.Count);
        Assert.NotEqual(interactions[0].GetProperty("id").GetString(), interactions[1].GetProperty("id").GetString());
        Assert.All(interactions, i => Assert.Equal("0af7651916cd43dd8448eb211c80319c", i.GetProperty("traceId").GetString()));
    }

    /// <summary>Sources that fail in the one place a test asks: every table reads as alice's rows, but one of them throws, or answers a key twice.</summary>
    private sealed class FailingOn(string? failure = null, string? twoRowsFor = null) : ISourceLogins
    {
        public ISourceSessions SessionsFor(Person person, PresentedToken token, string lookupId) => new PersonSessions(
            new Dictionary<string, SourceOpener> { ["mes"] = _ => ValueTask.FromResult<ISourceSession>(new Session(failure, twoRowsFor)) });

        private sealed class Session(string? failure, string? twoRowsFor) : ISourceSession
        {
            private static readonly Dictionary<string, SourceRow> Rows = new()
            {
                ["work_orders"] = FakeLogins.Row("WO-3", ("note", "n"), ("item_code", "I-200")),
                ["items"] = FakeLogins.Row("I-200", ("name", "Bracket")),
            };

            public ValueTask<IReadOnlyList<SourceRow>> ReadAsync(string table, string keyColumn, string key, IReadOnlyList<string> columns, CancellationToken cancellationToken = default)
            {
                if (table == failure)
                {
                    throw new IOException("the source went away");
                }

                var row = new SourceRow(Rows[table].Key, columns.ToDictionary(c => c, c => Rows[table].Values[c]));
                return ValueTask.FromResult<IReadOnlyList<SourceRow>>(table == twoRowsFor ? [row, row] : [row]);
            }

            public ValueTask<IReadOnlyList<SourceRow>> FindAsync(string table, string keyColumn, string column, string value, IReadOnlyList<string> columns, int limit, CancellationToken cancellationToken = default) =>
                ValueTask.FromResult<IReadOnlyList<SourceRow>>([]);

            public IAsyncEnumerable<SourceRow> ScanAsync(string table, string keyColumn, IReadOnlyList<string> columns, CancellationToken cancellationToken = default) =>
                AsyncEnumerable.Empty<SourceRow>();

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
