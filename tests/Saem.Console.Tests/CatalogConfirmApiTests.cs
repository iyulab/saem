using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Saem.Console.Tests;

/// <summary>
/// saem D-88 C4 · D-91: a builder's draft goes into force only as the one that was shown, against the catalog the builder
/// started from, with breaking changes acknowledged — and every confirmation leaves a line saying who made it.
/// </summary>
public sealed class CatalogConfirmApiTests : IAsyncLifetime
{
    private const string InForce = """
        [
          { "kind": "entityType", "name": "Line", "system": "mes", "table": "lines", "key": "line_id", "fields": { "name": "name" } },
          { "kind": "entityType", "name": "Equipment", "system": "mes", "table": "equipment", "key": "equip_id", "fields": { "line": "line_id" } },
          { "kind": "relation", "name": "on_line", "from": "Equipment", "via": "line", "to": "Line" }
        ]
        """;

    // The same catalog and one more type: only vocabulary is added.
    private const string Additive = """
        [
          { "kind": "entityType", "name": "Line", "system": "mes", "table": "lines", "key": "line_id", "fields": { "name": "name" } },
          { "kind": "entityType", "name": "Equipment", "system": "mes", "table": "equipment", "key": "equip_id", "fields": { "line": "line_id" } },
          { "kind": "entityType", "name": "Shift", "system": "mes", "table": "shifts", "key": "shift_id", "fields": { "name": "name" } },
          { "kind": "relation", "name": "on_line", "from": "Equipment", "via": "line", "to": "Line" }
        ]
        """;

    // The relation is taken away: a lookup that worked may now be refused.
    private const string Breaking = """
        [
          { "kind": "entityType", "name": "Line", "system": "mes", "table": "lines", "key": "line_id", "fields": { "name": "name" } },
          { "kind": "entityType", "name": "Equipment", "system": "mes", "table": "equipment", "key": "equip_id", "fields": { "line": "line_id" } }
        ]
        """;

    private ConsoleServer _server = null!;
    private string _draft = "";
    private string _confirmations = "";

    public async ValueTask InitializeAsync()
    {
        _draft = Path.GetTempFileName();
        _confirmations = Path.Combine(Path.GetTempPath(), $"confirmations-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(_draft, Additive, TestContext.Current.CancellationToken);
        _server = await ConsoleServer.StartAsync(
            ontologyJson: InForce,
            settings: new Dictionary<string, string?> { ["Saem:Console:Draft"] = _draft, ["Saem:Console:Confirmations"] = _confirmations });
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        File.Delete(_draft);
        File.Delete(_confirmations);
    }

    [Fact]
    public async Task The_draft_is_read_against_the_catalog_in_force_with_breaking_changes_first()
    {
        await File.WriteAllTextAsync(_draft, Breaking, TestContext.Current.CancellationToken);
        using var builder = _server.Client(ConsoleServer.Token("alice", ["ontology-builders"]));

        var view = await builder.GetFromJsonAsync<JsonElement>("api/catalog/draft", TestContext.Current.CancellationToken);

        Assert.Equal(await VersionInForce(builder), view.GetProperty("inForce").GetString());
        Assert.NotEqual(view.GetProperty("inForce").GetString(), view.GetProperty("draft").GetString());
        Assert.Equal(1, view.GetProperty("breaking").GetInt32());
        var change = view.GetProperty("changes").EnumerateArray().Single();
        Assert.Equal("breaking", change.GetProperty("kind").GetString());
        Assert.Equal("relation on_line", change.GetProperty("declaration").GetString());
    }

    [Fact]
    public async Task Confirming_replaces_the_catalog_with_the_draft_that_was_shown_and_records_who_did_it()
    {
        using var builder = _server.Client(ConsoleServer.Token("alice", ["ontology-builders"]));
        var view = await builder.GetFromJsonAsync<JsonElement>("api/catalog/draft", TestContext.Current.CancellationToken);

        using var response = await builder.PostAsJsonAsync(
            "api/catalog/confirm",
            new { @base = view.GetProperty("inForce").GetString(), draft = view.GetProperty("draft").GetString() },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Additive, await File.ReadAllTextAsync(_server.OntologyFile, TestContext.Current.CancellationToken));
        Assert.Equal(view.GetProperty("draft").GetString(), await VersionInForce(builder));
        var line = JsonDocument.Parse(Assert.Single(await File.ReadAllLinesAsync(_confirmations, TestContext.Current.CancellationToken))).RootElement;
        Assert.Equal("alice", line.GetProperty("by").GetString());
        Assert.Equal(view.GetProperty("inForce").GetString(), line.GetProperty("from").GetString());
        Assert.Equal(view.GetProperty("draft").GetString(), line.GetProperty("to").GetString());
        Assert.False(line.GetProperty("acknowledgedBreaking").GetBoolean());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_server.OntologyFile)!, $".{Path.GetFileName(_server.OntologyFile)}.*.confirm"));
    }

    [Fact]
    public async Task A_catalog_that_changed_since_the_builder_started_is_not_replaced()
    {
        using var builder = _server.Client(ConsoleServer.Token("alice", ["ontology-builders"]));
        var view = await builder.GetFromJsonAsync<JsonElement>("api/catalog/draft", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(_server.OntologyFile, Breaking, TestContext.Current.CancellationToken);

        using var response = await builder.PostAsJsonAsync(
            "api/catalog/confirm",
            new { @base = view.GetProperty("inForce").GetString(), draft = view.GetProperty("draft").GetString() },
            TestContext.Current.CancellationToken);

        await AssertRefused(response, "stale-base");
        Assert.Equal(Breaking, await File.ReadAllTextAsync(_server.OntologyFile, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(_confirmations) && new FileInfo(_confirmations).Length > 0);
    }

    [Fact]
    public async Task A_draft_that_changed_since_the_builder_read_it_is_not_confirmed()
    {
        using var builder = _server.Client(ConsoleServer.Token("alice", ["ontology-builders"]));
        var view = await builder.GetFromJsonAsync<JsonElement>("api/catalog/draft", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(_draft, Breaking, TestContext.Current.CancellationToken);

        using var response = await builder.PostAsJsonAsync(
            "api/catalog/confirm",
            new { @base = view.GetProperty("inForce").GetString(), draft = view.GetProperty("draft").GetString(), acknowledgeBreaking = true },
            TestContext.Current.CancellationToken);

        await AssertRefused(response, "stale-draft");
        Assert.Equal(InForce, await File.ReadAllTextAsync(_server.OntologyFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Breaking_changes_go_in_force_only_when_the_builder_acknowledges_them()
    {
        await File.WriteAllTextAsync(_draft, Breaking, TestContext.Current.CancellationToken);
        using var builder = _server.Client(ConsoleServer.Token("alice", ["ontology-builders"]));
        var view = await builder.GetFromJsonAsync<JsonElement>("api/catalog/draft", TestContext.Current.CancellationToken);
        var ask = new { @base = view.GetProperty("inForce").GetString(), draft = view.GetProperty("draft").GetString() };

        using var refused = await builder.PostAsJsonAsync("api/catalog/confirm", ask, TestContext.Current.CancellationToken);
        await AssertRefused(refused, "breaking");
        Assert.Equal(InForce, await File.ReadAllTextAsync(_server.OntologyFile, TestContext.Current.CancellationToken));

        using var acknowledged = await builder.PostAsJsonAsync("api/catalog/confirm", new { ask.@base, ask.draft, acknowledgeBreaking = true }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, acknowledged.StatusCode);
        Assert.Equal(Breaking, await File.ReadAllTextAsync(_server.OntologyFile, TestContext.Current.CancellationToken));
        var line = JsonDocument.Parse(Assert.Single(await File.ReadAllLinesAsync(_confirmations, TestContext.Current.CancellationToken))).RootElement;
        Assert.True(line.GetProperty("acknowledgedBreaking").GetBoolean());
        Assert.Equal(1, line.GetProperty("breaking").GetInt32());
    }

    [Fact]
    public async Task A_draft_that_declares_what_is_in_force_has_nothing_to_confirm()
    {
        await File.WriteAllTextAsync(_draft, InForce, TestContext.Current.CancellationToken);
        using var builder = _server.Client(ConsoleServer.Token("alice", ["ontology-builders"]));
        var view = await builder.GetFromJsonAsync<JsonElement>("api/catalog/draft", TestContext.Current.CancellationToken);

        using var response = await builder.PostAsJsonAsync(
            "api/catalog/confirm",
            new { @base = view.GetProperty("inForce").GetString(), draft = view.GetProperty("draft").GetString() },
            TestContext.Current.CancellationToken);

        await AssertRefused(response, "nothing-to-confirm");
    }

    [Fact]
    public async Task A_draft_the_ontology_refuses_is_shown_with_why_and_never_confirmed()
    {
        await File.WriteAllTextAsync(_draft, """[ { "kind": "relation", "name": "r", "from": "Nowhere", "via": "x", "to": "Nowhere" } ]""", TestContext.Current.CancellationToken);
        using var builder = _server.Client(ConsoleServer.Token("alice", ["ontology-builders"]));

        using var shown = await builder.GetAsync("api/catalog/draft", TestContext.Current.CancellationToken);
        using var confirmed = await builder.PostAsJsonAsync("api/catalog/confirm", new { @base = "a", draft = "b" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, shown.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, confirmed.StatusCode);
        Assert.Equal(InForce, await File.ReadAllTextAsync(_server.OntologyFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Two_builders_confirming_from_the_same_catalog_cannot_both_win()
    {
        using var alice = _server.Client(ConsoleServer.Token("alice", ["ontology-builders"]));
        using var bob = _server.Client(ConsoleServer.Token("bob", ["ontology-builders"]));
        var view = await alice.GetFromJsonAsync<JsonElement>("api/catalog/draft", TestContext.Current.CancellationToken);
        var ask = new { @base = view.GetProperty("inForce").GetString(), draft = view.GetProperty("draft").GetString() };

        var both = await Task.WhenAll(
            alice.PostAsJsonAsync("api/catalog/confirm", ask, TestContext.Current.CancellationToken),
            bob.PostAsJsonAsync("api/catalog/confirm", ask, TestContext.Current.CancellationToken));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], both.Select(r => r.StatusCode).Order());
        Assert.Single(await File.ReadAllLinesAsync(_confirmations, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Only_builders_see_or_confirm_a_draft()
    {
        using var auditor = _server.Client(ConsoleServer.Token("ann", ["privacy-office"]));
        using var anonymous = _server.Client();

        using var seen = await auditor.GetAsync("api/catalog/draft", TestContext.Current.CancellationToken);
        using var asked = await auditor.PostAsJsonAsync("api/catalog/confirm", new { @base = "a", draft = "b" }, TestContext.Current.CancellationToken);
        using var unknown = await anonymous.GetAsync("api/catalog/draft", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, seen.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
    }

    [Fact]
    public async Task A_console_without_a_draft_setting_confirms_nothing()
    {
        await using var plain = await ConsoleServer.StartAsync(ontologyJson: InForce);
        using var builder = plain.Client(ConsoleServer.Token("alice", ["ontology-builders"]));

        using var shown = await builder.GetAsync("api/catalog/draft", TestContext.Current.CancellationToken);
        using var confirmed = await builder.PostAsJsonAsync("api/catalog/confirm", new { @base = "a", draft = "b" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, shown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, confirmed.StatusCode);
    }

    private static async Task<string> VersionInForce(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("api/catalog", TestContext.Current.CancellationToken)).GetProperty("version").GetString()!;

    private static async Task AssertRefused(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(code, problem.GetProperty("code").GetString());
    }
}
