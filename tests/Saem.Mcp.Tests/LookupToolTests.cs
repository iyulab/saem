using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Saem.Mcp.Tests;

/// <summary>
/// The tool over HTTP as a client sees it: who it runs as comes from the token alone, the same call
/// answers differently for different people, and a request without a valid token is turned away.
/// </summary>
public sealed class LookupToolTests : IAsyncLifetime
{
    private const string Canary = "CANARY-line2-only";

    private const string OntologyJson = """
        [
          { "kind": "entityType", "name": "WorkOrder", "system": "mes", "table": "work_orders", "key": "wo_no",
            "fields": { "note": "note", "item": "item_code" } },
          { "kind": "entityType", "name": "Item", "system": "mes", "table": "items", "key": "item_code",
            "fields": { "name": "name" } },
          { "kind": "relation", "name": "producesItem", "from": "WorkOrder", "via": "item", "to": "Item", "inverse": "producedBy" }
        ]
        """;

    private TestServer _server = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await TestServer.StartAsync(OntologyJson, new FakeLogins(new()
        {
            ["alice"] = new()
            {
                ["work_orders"] = [FakeLogins.Row("WO-3", ("note", Canary), ("item_code", "I-200"))],
                ["items"] = [FakeLogins.Row("I-200", ("name", "Bracket"))],
            },
            ["bob"] = new(),
        }, failingPerson: "dave"));
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    private static Dictionary<string, object?> Call(string type, string key, string[] path, string[] read) => new()
    {
        ["start"] = new Dictionary<string, object?> { ["type"] = type, ["key"] = key },
        ["path"] = path,
        ["read"] = read,
    };

    [Fact]
    public async Task The_one_tool_lists_the_declared_vocabulary()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var client = await _server.ClientAsync(TestServer.Token("alice"), ct);

        var tool = Assert.Single(await client.ListToolsAsync(cancellationToken: ct));

        Assert.Equal("lookup", tool.Name);
        Assert.Contains("WorkOrder (item, note)", tool.Description, StringComparison.Ordinal);
        Assert.Contains("producesItem: WorkOrder -> Item", tool.Description, StringComparison.Ordinal);
        var types = tool.JsonSchema.GetProperty("properties").GetProperty("start").GetProperty("properties").GetProperty("type").GetProperty("enum");
        Assert.Equal(["Item", "WorkOrder"], types.EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("producedBy: Item -> WorkOrder (producesItem backwards)", tool.Description, StringComparison.Ordinal);
        var relations = tool.JsonSchema.GetProperty("properties").GetProperty("path").GetProperty("items").GetProperty("enum");
        Assert.Equal(["producedBy", "producesItem"], relations.EnumerateArray().Select(e => e.GetString()));
    }

    /// <summary>
    /// A group store that cannot be reached is a failure of that store — an error that says so, not a refusal
    /// and not a source's failure — and its detail stays in the log.
    /// </summary>
    [Fact]
    public async Task A_group_store_that_cannot_be_reached_is_a_failure_of_that_store_not_a_refusal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await TestServer.StartAsync(OntologyJson, new FakeLogins(new() { ["alice"] = new() }), new Dictionary<string, string?>
        {
            ["Saem:Materialization:BaseIri"] = "https://example.org/ontology/",
            // The discard port: nothing listens there.
            ["Saem:Materialization:Groups:g-plant:Url"] = "http://127.0.0.1:9/",
        });
        await using var alice = await server.ClientAsync(TestServer.Token("alice", groups: ["g-plant"]), ct);

        var result = await alice.CallToolAsync("lookup", Call("Item", "I-200", ["producedBy"], ["note"]), cancellationToken: ct);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("materialized relations of the group 'g-plant' could not be read", text, StringComparison.Ordinal);
        Assert.Contains("not a refusal", text, StringComparison.Ordinal);
        Assert.Contains(server.Logs.Lines, l => l.Contains("failed", StringComparison.Ordinal));
    }

    /// <summary>
    /// T-41 through the tool: the server keeps no group's partition for this person (it keeps none at all),
    /// so a relation walked backwards is refused for that reason — an answer, not an error.
    /// </summary>
    [Fact]
    public async Task A_relation_walked_backwards_for_a_person_in_no_kept_group_is_refused_for_that_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alice = await _server.ClientAsync(TestServer.Token("alice"), ct);

        var result = await alice.CallToolAsync("lookup", Call("Item", "I-200", ["producedBy"], ["note"]), cancellationToken: ct);

        Assert.NotEqual(true, result.IsError);
        var refusal = result.StructuredContent!.Value.GetProperty("refusal");
        Assert.Equal("noMaterializedPartition", refusal.GetProperty("reason").GetString());
        Assert.Equal("producedBy", refusal.GetProperty("relation").GetString());
    }

    [Fact]
    public async Task The_same_call_answers_as_the_person_the_token_names()
    {
        var ct = TestContext.Current.CancellationToken;
        var call = Call("WorkOrder", "WO-3", [], ["note"]);

        await using var alice = await _server.ClientAsync(TestServer.Token("alice"), ct);
        var forAlice = await alice.CallToolAsync("lookup", call, cancellationToken: ct);
        await using var bob = await _server.ClientAsync(TestServer.Token("bob"), ct);
        var forBob = await bob.CallToolAsync("lookup", call, cancellationToken: ct);

        Assert.NotEqual(true, forAlice.IsError);
        var answer = forAlice.StructuredContent!.Value;
        Assert.Equal("answered", answer.GetProperty("kind").GetString());
        var claim = Assert.Single(answer.GetProperty("claims").EnumerateArray());
        Assert.Equal(Canary, claim.GetProperty("value").GetString());
        Assert.Equal("work_orders", Assert.Single(claim.GetProperty("sources").EnumerateArray()).GetProperty("table").GetString());

        Assert.NotEqual(true, forBob.IsError);
        var refused = forBob.StructuredContent!.Value;
        Assert.Equal("refused", refused.GetProperty("kind").GetString());
        Assert.Equal("noVisibleResult", refused.GetProperty("refusal").GetProperty("reason").GetString());
        Assert.DoesNotContain(Canary, refused.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_walk_follows_relations_and_names_the_hops()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alice = await _server.ClientAsync(TestServer.Token("alice"), ct);

        var result = await alice.CallToolAsync("lookup", Call("WorkOrder", "WO-3", ["producesItem"], ["name"]), cancellationToken: ct);

        var claim = Assert.Single(result.StructuredContent!.Value.GetProperty("claims").EnumerateArray());
        Assert.Equal("Bracket", claim.GetProperty("value").GetString());
        var hop = Assert.Single(claim.GetProperty("path").EnumerateArray());
        Assert.Equal("producesItem", hop.GetProperty("relation").GetString());
        // The text content carries the same contract JSON, for clients that do not read structured content.
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(JsonElement.DeepEquals(result.StructuredContent!.Value, JsonDocument.Parse(text).RootElement), "the text carries the same JSON, escaped as its writer chose");
    }

    [Fact]
    public async Task A_call_outside_the_vocabulary_is_a_tool_error_not_a_refusal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alice = await _server.ClientAsync(TestServer.Token("alice"), ct);

        var result = await alice.CallToolAsync("lookup", Call("WorkOrder", "WO-3", [], ["salary"]), cancellationToken: ct);

        Assert.True(result.IsError);
        Assert.Null(result.StructuredContent);
        Assert.Contains("salary", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_that_names_nobody_runs_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var nobody = await _server.ClientAsync(TestServer.Token(subject: null), ct);

        var result = await nobody.CallToolAsync("lookup", Call("WorkOrder", "WO-3", [], ["note"]), cancellationToken: ct);

        Assert.True(result.IsError);
        Assert.Contains("'sub'", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_person_with_no_login_to_the_source_gets_a_tool_error_that_says_so()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var carol = await _server.ClientAsync(TestServer.Token("carol"), ct);

        var result = await carol.CallToolAsync("lookup", Call("WorkOrder", "WO-3", [], ["note"]), cancellationToken: ct);

        Assert.True(result.IsError);
        Assert.Null(result.StructuredContent);
        Assert.Contains("'mes'", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_source_is_a_tool_error_that_keeps_its_detail_in_the_log()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var dave = await _server.ClientAsync(TestServer.Token("dave"), ct);

        var result = await dave.CallToolAsync("lookup", Call("WorkOrder", "WO-3", [], ["note"]), cancellationToken: ct);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("not a refusal", text, StringComparison.Ordinal);
        Assert.DoesNotContain("db-host", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("someone-else")]
    public async Task A_request_without_a_token_for_this_server_is_turned_away_and_told_where_to_get_one(string? audience)
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, _server.Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (audience is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestServer.Token("alice", audience));
        }

        using var response = await http.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Contains("resource_metadata", challenge.Parameter, StringComparison.Ordinal);
    }
}

public class PersonTests
{
    [Fact]
    public void The_person_is_sub_and_the_agent_is_the_sub_of_act()
    {
        var person = Person.From(Claims.Of(("sub", "alice", "string"), ("act", """{"sub":"agent-7"}""", "JSON")));

        Assert.Equal(new Person("alice", "agent-7"), person);
    }

    [Fact]
    public void Without_act_the_person_called_directly()
    {
        Assert.Equal(new Person("bob", null), Person.From(Claims.Of(("sub", "bob", "string"))));
    }

    /// <summary>A token's groups claim — one value or several — is the person's groups, each once and in name order.</summary>
    [Fact]
    public void The_groups_are_the_tokens_groups_claim()
    {
        var person = Person.From(Claims.Of(("sub", "alice", "string"), ("groups", "g-plant", "string"), ("groups", "g-office", "string"), ("groups", "g-plant", "string")));

        Assert.Equal(["g-office", "g-plant"], person!.Groups);
        Assert.Equal(new Person("alice", null) { Groups = ["g-office", "g-plant"] }, person);
        Assert.NotEqual(new Person("alice", null), person);
        Assert.Empty(Person.From(Claims.Of(("sub", "bob", "string")))!.Groups);
    }

    [Fact]
    public void Without_sub_there_is_no_person()
    {
        Assert.Null(Person.From(Claims.Of(("act", """{"sub":"agent-7"}""", "JSON"))));
        Assert.Null(Person.From(null));
    }
}

public class StartupTests
{
    [Fact]
    public async Task A_server_without_an_issuer_does_not_start()
    {
        var failed = await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(() =>
            TestServer.StartAsync("[]", new Dictionary<string, string?> { ["Saem:Identity:Audience"] = "saem" }));

        Assert.Contains("Authority", failed.Message, StringComparison.Ordinal);
    }
}
