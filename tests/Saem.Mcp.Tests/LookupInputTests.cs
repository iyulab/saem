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

    private static Dictionary<string, object?> Call(string[] read, string key = "WO-3") => new()
    {
        ["start"] = new Dictionary<string, object?> { ["type"] = "WorkOrder", ["key"] = key },
        ["path"] = Array.Empty<string>(),
        ["read"] = read,
    };

    /// <summary>
    /// A field named twice is the same field: reading it again asks for nothing more, so the answer holds each fact once
    /// however many times the call named it — an answer that grew with the repeats would be counted by a caller that adds
    /// values up, and a call that repeats a name thousands of times would be answered with megabytes.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(5000)]
    public async Task A_field_named_more_than_once_is_read_once(int times)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await TestServer.StartAsync(OntologyJson, Alice());
        await using var alice = await server.ClientAsync(TestServer.Token("alice"), ct);

        var once = await alice.CallToolAsync("lookup", Call(["note"]), cancellationToken: ct);
        var repeated = await alice.CallToolAsync("lookup", Call([.. Enumerable.Repeat("note", times)]), cancellationToken: ct);

        Assert.True(once.IsError is not true);
        Assert.True(repeated.IsError is not true);
        Assert.Equal(
            once.StructuredContent!.Value.GetProperty("claims").GetArrayLength(),
            repeated.StructuredContent!.Value.GetProperty("claims").GetArrayLength());
        static string[] Facts(CallToolResult result) =>
            [.. result.StructuredContent!.Value.GetProperty("claims").EnumerateArray().Select(c => $"{c.GetProperty("field").GetString()}={c.GetProperty("value").GetString()}")];
        Assert.Equal(Facts(once), Facts(repeated));
    }

    /// <summary>
    /// A key or name this long is not a key or a name: it is said to be a call that cannot be run, not reported as a source
    /// that failed — the walker would otherwise carry the text to the sources.
    /// </summary>
    [Fact]
    public async Task A_key_longer_than_any_key_is_a_call_error_and_not_a_source_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await TestServer.StartAsync(OntologyJson, Alice());
        await using var alice = await server.ClientAsync(TestServer.Token("alice"), ct);

        var result = await alice.CallToolAsync("lookup", Call(["note"], key: new string('9', 100_000)), cancellationToken: ct);

        Assert.True(result.IsError);
        var message = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("1000", message, StringComparison.Ordinal);
        Assert.DoesNotContain("failure", message, StringComparison.OrdinalIgnoreCase);
        Assert.True(message.Length < 500);
        Assert.DoesNotContain(server.Logs.Lines, l => l.Contains("failed at a source", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_name_longer_than_any_name_is_a_call_error_that_does_not_repeat_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await TestServer.StartAsync(OntologyJson, Alice());
        await using var alice = await server.ClientAsync(TestServer.Token("alice"), ct);
        var long_ = new string('f', 100_000);

        var result = await alice.CallToolAsync("lookup", Call([long_]), cancellationToken: ct);

        Assert.True(result.IsError);
        var message = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.DoesNotContain(long_, message, StringComparison.Ordinal);
        Assert.True(message.Length < 500);
    }

    /// <summary>The message names the member the call should not have sent; the contract types behind it are the server's own business.</summary>
    [Theory]
    [InlineData("limit", false)]
    [InlineData("keys", true)]
    public async Task A_member_the_call_does_not_declare_is_named_without_the_names_of_the_servers_own_types(string stray, bool inStart)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await TestServer.StartAsync(OntologyJson, Alice());
        await using var alice = await server.ClientAsync(TestServer.Token("alice"), ct);
        var call = Call(["note"]);
        if (inStart)
        {
            ((Dictionary<string, object?>)call["start"]!)[stray] = new[] { "WO-9" };
        }
        else
        {
            call[stray] = 1;
        }

        var result = await alice.CallToolAsync("lookup", call, cancellationToken: ct);

        Assert.True(result.IsError);
        var message = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains(stray, message, StringComparison.Ordinal);
        Assert.DoesNotContain("Saem.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("LookupCall", message, StringComparison.Ordinal);
        Assert.DoesNotContain("LookupStart", message, StringComparison.Ordinal);
    }
}
