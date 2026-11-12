using ModelContextProtocol.Protocol;
using Xunit;

namespace Saem.Mcp.Tests;

/// <summary>A call that is not shaped like a lookup is said so — it is not an outage of a source it never reached.</summary>
public sealed class LookupInputTests
{
    private const string OntologyJson = """
        [
          { "kind": "entityType", "name": "WorkOrder", "system": "mes", "table": "work_orders", "key": "wo_no",
            "fields": { "note": "note", "item": "item_code" } }
        ]
        """;

    private static FakeLogins Alice() => new(new()
    {
        ["alice"] = new() { ["work_orders"] = [FakeLogins.Row("WO-3", ("note", "n"), ("item_code", "I-200"))] },
    });

    private static Dictionary<string, object?> WithStart(Dictionary<string, object?> start) => new()
    {
        ["start"] = start,
        ["path"] = Array.Empty<string>(),
        ["read"] = new[] { "note" },
    };

    public static TheoryData<string, Dictionary<string, object?>> Malformed => new()
    {
        { "a where naming nothing", new() { ["type"] = "WorkOrder", ["where"] = new Dictionary<string, object?>() } },
        { "a where without a value", new() { ["type"] = "WorkOrder", ["where"] = new Dictionary<string, object?> { ["field"] = "item" } } },
        { "a where without a field", new() { ["type"] = "WorkOrder", ["where"] = new Dictionary<string, object?> { ["value"] = "I-200" } } },
        { "a start without a type", new() { ["key"] = "WO-3" } },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task A_start_that_is_not_shaped_like_one_is_a_call_error_and_not_a_source_failure(string _, Dictionary<string, object?> start)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await TestServer.StartAsync(OntologyJson, Alice());
        await using var alice = await server.ClientAsync(TestServer.Token("alice"), ct);

        var result = await alice.CallToolAsync("lookup", WithStart(start), cancellationToken: ct);

        Assert.True(result.IsError);
        var message = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.DoesNotContain("source failed", message, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.DoesNotContain(server.Logs.Lines, l => l.Contains("failed at a source", StringComparison.Ordinal));
    }
}
