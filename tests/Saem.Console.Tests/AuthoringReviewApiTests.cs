using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Saem.Ontology;
using Xunit;

namespace Saem.Console.Tests;

/// <summary>
/// saem D-88 C4: a builder reviews the declarations an authoring pass proposed one by one, and the draft file is the whole
/// state of the review — taking a proposal puts it in the draft, leaving one takes it out, a draft that moved since it was
/// read is refused, and a review that would leave the draft inconsistent is not made.
/// </summary>
public sealed class AuthoringReviewApiTests : IAsyncLifetime
{
    private const string Line = """{ "kind": "entityType", "name": "Line", "system": "mes", "table": "lines", "key": "line_id", "fields": { "name": "name" } }""";
    private const string Equipment = """{ "kind": "entityType", "name": "Equipment", "system": "mes", "table": "equipment", "key": "equip_id", "fields": { "line": "line_id" } }""";
    private const string Shift = """{ "kind": "entityType", "name": "Shift", "system": "mes", "table": "shifts", "key": "shift_id", "fields": { "name": "name" } }""";
    private const string OnLine = """{ "kind": "relation", "name": "on_line", "from": "Equipment", "via": "line", "to": "Line" }""";

    private static readonly string[] ShiftId = ["entityType:Shift"];
    private static readonly string[] EquipmentId = ["entityType:Equipment"];
    private static readonly string[] EquipmentAndRelation = ["entityType:Equipment", "relation:on_line"];

    private ConsoleServer _server = null!;
    private string _draft = "";
    private string _proposals = "";

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _draft = Path.GetTempFileName();
        _proposals = Path.GetTempFileName();

        // What the pass wrote: the catalog in force and everything it proposed on top of it.
        await File.WriteAllTextAsync(_draft, $"[{Line},{Equipment},{Shift},{OnLine}]", ct);
        await File.WriteAllTextAsync(_proposals, $$"""
            {
              "pass": "pass-20261030T010203000Z",
              "added": [{{Equipment}},{{Shift}},{{OnLine}}],
              "generation": { "model": "m", "proposerVersion": "0.23.0", "promptFingerprint": "a1b2c3d4", "startedAt": "2026-10-30T01:02:03.0000000Z", "modelCallsRetried": 0 }
            }
            """, ct);
        _server = await ConsoleServer.StartAsync(
            ontologyJson: $"[{Line}]",
            settings: new Dictionary<string, string?>
            {
                ["Saem:Console:Draft"] = _draft,
                ["Saem:Console:Proposals"] = _proposals,
                ["Saem:Console:Confirmations"] = Path.Combine(Path.GetTempPath(), $"confirmations-{Guid.NewGuid():N}.jsonl"),
            });
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        File.Delete(_draft);
        File.Delete(_proposals);
    }

    [Fact]
    public async Task The_proposals_are_shown_against_the_draft_with_what_the_pass_was_made_with()
    {
        using var builder = Builder();

        var view = await builder.GetFromJsonAsync<JsonElement>("api/authoring/review", TestContext.Current.CancellationToken);

        Assert.Equal("pass-20261030T010203000Z", view.GetProperty("pass").GetString());
        Assert.Equal("m", view.GetProperty("generation").GetProperty("model").GetString());
        Assert.Equal(["entityType:Equipment", "entityType:Shift", "relation:on_line"], view.GetProperty("proposals").EnumerateArray().Select(p => p.GetProperty("id").GetString()));
        Assert.All(view.GetProperty("proposals").EnumerateArray(), p => Assert.Equal("taken", p.GetProperty("state").GetString()));
        Assert.Equal("Shift", view.GetProperty("proposals")[1].GetProperty("declaration").GetProperty("name").GetString());
        Assert.NotEqual(view.GetProperty("inForce").GetString(), view.GetProperty("draft").GetString());
    }

    [Fact]
    public async Task Leaving_a_proposal_takes_it_out_of_the_draft_and_taking_it_again_puts_it_back()
    {
        using var builder = Builder();
        var view = await Read(builder);

        var left = await Review(builder, view.Draft, new { leave = ShiftId });

        Assert.Equal(HttpStatusCode.OK, left.Status);
        Assert.Equal("left", State(left.View, "entityType:Shift"));
        Assert.Equal("taken", State(left.View, "entityType:Equipment"));
        Assert.NotEqual(view.Draft, left.View.GetProperty("draft").GetString());
        var kept = JsonSerializer.Deserialize(await File.ReadAllTextAsync(_draft, TestContext.Current.CancellationToken), OntologyJson.Default.IReadOnlyListDeclaration)!;
        Assert.Equal(["Line", "Equipment", "on_line"], kept.Select(d => d.Name));

        var taken = await Review(builder, left.View.GetProperty("draft").GetString()!, new { take = ShiftId });

        Assert.Equal("taken", State(taken.View, "entityType:Shift"));
        Assert.Equal(view.Draft, taken.View.GetProperty("draft").GetString());
        Assert.Equal($"[{Line}]", await File.ReadAllTextAsync(_server.OntologyFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_proposal_the_draft_holds_changed_is_shown_edited_with_both_and_taking_it_does_not_overwrite_the_edit()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(_draft, $"[{Line},{Equipment},{Shift.Replace("shifts", "work_shifts")},{OnLine}]", ct);
        using var builder = Builder();
        var view = await builder.GetFromJsonAsync<JsonElement>("api/authoring/review", ct);

        var shift = view.GetProperty("proposals").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "entityType:Shift");
        Assert.Equal("edited", shift.GetProperty("state").GetString());
        Assert.Equal("shifts", shift.GetProperty("declaration").GetProperty("table").GetString());
        Assert.Equal("work_shifts", shift.GetProperty("current").GetProperty("table").GetString());

        var again = await Review(builder, view.GetProperty("draft").GetString()!, new { take = ShiftId });

        Assert.Equal("edited", State(again.View, "entityType:Shift"));
        Assert.Contains("work_shifts", await File.ReadAllTextAsync(_draft, ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_review_that_would_leave_the_draft_inconsistent_is_not_made()
    {
        using var builder = Builder();
        var view = await Read(builder);
        var before = await File.ReadAllTextAsync(_draft, TestContext.Current.CancellationToken);

        // on_line joins Equipment to Line: Equipment cannot be left while the relation stays.
        var refused = await Review(builder, view.Draft, new { leave = EquipmentId });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.Status);
        Assert.Equal("inconsistent", refused.View.GetProperty("code").GetString());
        Assert.Equal(before, await File.ReadAllTextAsync(_draft, TestContext.Current.CancellationToken));

        var together = await Review(builder, view.Draft, new { leave = EquipmentAndRelation });

        Assert.Equal(HttpStatusCode.OK, together.Status);
        Assert.Equal("left", State(together.View, "relation:on_line"));
    }

    [Fact]
    public async Task A_draft_that_moved_since_it_was_read_is_not_reviewed()
    {
        using var builder = Builder();
        var view = await Read(builder);
        await File.WriteAllTextAsync(_draft, $"[{Line},{Equipment},{OnLine}]", TestContext.Current.CancellationToken);

        var refused = await Review(builder, view.Draft, new { leave = ShiftId });

        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("stale-draft", refused.View.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("""{ "draft": "v", "take": ["entityType:Nobody"] }""", HttpStatusCode.BadRequest, "unknown-proposal")]
    [InlineData("""{ "draft": "v", "take": ["entityType:Shift"], "leave": ["entityType:Shift"] }""", HttpStatusCode.BadRequest, "contradiction")]
    [InlineData("""{ "draft": "v" }""", HttpStatusCode.BadRequest, null)]
    [InlineData("""{ "take": ["entityType:Shift"] }""", HttpStatusCode.BadRequest, null)]
    [InlineData("""{ "draft": "v", "take": "entityType:Shift" }""", HttpStatusCode.BadRequest, null)]
    public async Task A_review_that_is_not_one_is_refused(string body, HttpStatusCode status, string? code)
    {
        using var builder = Builder();
        var view = await Read(builder);
        using var content = new StringContent(body.Replace("\"v\"", $"\"{view.Draft}\"", StringComparison.Ordinal), System.Text.Encoding.UTF8, "application/json");

        using var response = await builder.PostAsync("api/authoring/review", content, TestContext.Current.CancellationToken);

        Assert.Equal(status, response.StatusCode);
        if (code is not null)
        {
            Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Only_builders_review_and_a_console_without_proposals_has_nothing_to_review()
    {
        using var auditor = _server.Client(ConsoleServer.Token("ann", ["privacy-office"]));
        using var seen = await auditor.GetAsync("api/authoring/review", TestContext.Current.CancellationToken);
        using var asked = await auditor.PostAsJsonAsync("api/authoring/review", new { draft = "a", take = ShiftId }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, seen.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asked.StatusCode);

        await using var bare = await ConsoleServer.StartAsync(
            ontologyJson: $"[{Line}]",
            settings: new Dictionary<string, string?> { ["Saem:Console:Draft"] = _draft, ["Saem:Console:Confirmations"] = Path.Combine(Path.GetTempPath(), $"confirmations-{Guid.NewGuid():N}.jsonl") });
        using var builder = bare.Client(ConsoleServer.Token("alice", ["ontology-builders"]));
        using var none = await builder.GetAsync("api/authoring/review", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
    }

    [Fact]
    public async Task Proposals_without_the_draft_they_belong_to_are_refused_at_start()
    {
        var refused = await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(() =>
            ConsoleServer.StartAsync(ontologyJson: $"[{Line}]", settings: new Dictionary<string, string?> { ["Saem:Console:Proposals"] = _proposals }));

        Assert.Contains("needs Saem:Console:Draft", refused.Message, StringComparison.Ordinal);
    }

    private HttpClient Builder() => _server.Client(ConsoleServer.Token("alice", ["ontology-builders"]));

    private static async Task<(string Draft, JsonElement View)> Read(HttpClient builder)
    {
        var view = await builder.GetFromJsonAsync<JsonElement>("api/authoring/review", TestContext.Current.CancellationToken);
        return (view.GetProperty("draft").GetString()!, view);
    }

    private static async Task<(HttpStatusCode Status, JsonElement View)> Review(HttpClient builder, string draft, object decision)
    {
        var body = JsonSerializer.SerializeToNode(decision)!.AsObject();
        body["draft"] = draft;
        using var response = await builder.PostAsJsonAsync("api/authoring/review", body, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken));
    }

    private static string State(JsonElement view, string id) =>
        view.GetProperty("proposals").EnumerateArray().Single(p => p.GetProperty("id").GetString() == id).GetProperty("state").GetString()!;
}
