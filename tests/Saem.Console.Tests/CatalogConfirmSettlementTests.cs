using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Saem.Console.Tests;

/// <summary>
/// Confirming the draft of a reviewed pass also records what the review took of the pass's proposals, in the form Gil reads
/// (saem D-88): one line per proposal, once per pass, by the first confirmation that follows — and a pass whose proposals
/// cannot be read is not confirmed into a catalog whose review goes unrecorded.
/// </summary>
public sealed class CatalogConfirmSettlementTests : IAsyncLifetime
{
    private const string Line = """{ "kind": "entityType", "name": "Line", "system": "mes", "table": "lines", "key": "line_id", "fields": { "name": "name" } }""";
    private const string Equipment = """{ "kind": "entityType", "name": "Equipment", "system": "mes", "table": "equipment", "key": "equip_id", "fields": { "line": "line_id" } }""";
    private const string Shift = """{ "kind": "entityType", "name": "Shift", "system": "mes", "table": "shifts", "key": "shift_id", "fields": { "name": "name" } }""";
    private const string OnLine = """{ "kind": "relation", "name": "on_line", "from": "Equipment", "via": "line", "to": "Line" }""";
    private const string Pass = "pass-20261104T010203000Z";

    private ConsoleServer _server = null!;
    private string _draft = "";
    private string _proposals = "";
    private string _records = "";

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _draft = Path.GetTempFileName();
        _proposals = Path.GetTempFileName();
        _records = Path.Combine(Path.GetTempPath(), $"settled-{Guid.NewGuid():N}.jsonl");

        // The review left Shift out: the draft holds the catalog in force and two of the three proposals.
        await File.WriteAllTextAsync(_draft, $"[{Line},{Equipment},{OnLine}]", ct);
        await File.WriteAllTextAsync(_proposals, $$"""{ "pass": "{{Pass}}", "added": [{{Equipment}},{{Shift}},{{OnLine}}] }""", ct);
        _server = await ConsoleServer.StartAsync(
            ontologyJson: $"[{Line}]",
            settings: new Dictionary<string, string?>
            {
                ["Saem:Console:Draft"] = _draft,
                ["Saem:Console:Proposals"] = _proposals,
                ["Saem:Console:SettledRecords"] = _records,
                ["Saem:Console:Confirmations"] = Path.Combine(Path.GetTempPath(), $"confirmations-{Guid.NewGuid():N}.jsonl"),
            });
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        File.Delete(_draft);
        File.Delete(_proposals);
        File.Delete(_records);
    }

    [Fact]
    public async Task Confirming_records_one_line_per_proposal_as_taken_or_not_in_the_name_of_who_confirmed()
    {
        using var builder = Builder();

        var (status, body) = await Confirm(builder);

        Assert.Equal(HttpStatusCode.OK, status);
        var settlement = body.GetProperty("settlement");
        Assert.Equal(Pass, settlement.GetProperty("pass").GetString());
        Assert.True(settlement.GetProperty("recorded").GetBoolean());
        Assert.Equal((3, 2), (settlement.GetProperty("proposals").GetInt32(), settlement.GetProperty("taken").GetInt32()));

        var lines = (await File.ReadAllLinesAsync(_records, TestContext.Current.CancellationToken)).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Equal(["entityType:Equipment", "entityType:Shift", "relation:on_line"], lines.Select(l => l.GetProperty("id").GetString()));
        Assert.Equal(["yes", "no", "yes"], lines.Select(l => l.GetProperty("values").GetProperty("taken").GetString()));
        Assert.All(lines, l => Assert.Equal(Pass, l.GetProperty("batch").GetString()));
        Assert.All(lines, l => Assert.Equal("alice", l.GetProperty("settled_by").GetProperty("taken").GetString()));
    }

    [Fact]
    public async Task A_pass_already_recorded_is_not_recorded_again_by_a_later_confirmation()
    {
        using var builder = Builder();
        await Confirm(builder);
        var before = await File.ReadAllTextAsync(_records, TestContext.Current.CancellationToken);
        // The builder takes Shift after all and confirms again: the pass was settled by the first confirmation.
        await File.WriteAllTextAsync(_draft, $"[{Line},{Equipment},{Shift},{OnLine}]", TestContext.Current.CancellationToken);

        var (status, body) = await Confirm(builder);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("settlement").GetProperty("recorded").GetBoolean());
        Assert.Equal(before, await File.ReadAllTextAsync(_records, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Proposals_that_cannot_be_read_stop_the_confirmation_and_leave_the_catalog_as_it_was()
    {
        await File.WriteAllTextAsync(_proposals, "{ not json", TestContext.Current.CancellationToken);
        using var builder = Builder();

        var (status, _) = await Confirm(builder);

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal($"[{Line}]", await File.ReadAllTextAsync(_server.OntologyFile, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(_records));
    }

    [Fact]
    public async Task A_draft_written_by_hand_confirms_when_no_pass_has_left_proposals()
    {
        // No pass has written proposals: there is no review to settle, which is not the same as proposals that cannot be read
        // (the review screen says "there are no proposals" for the same file).
        File.Delete(_proposals);
        using var builder = Builder();

        var (status, body) = await Confirm(builder);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("settlement").ValueKind);
        Assert.Equal($"[{Line},{Equipment},{OnLine}]", await File.ReadAllTextAsync(_server.OntologyFile, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(_records));
    }

    [Fact]
    public async Task A_console_without_settled_records_confirms_without_a_settlement()
    {
        var plainDraft = Path.GetTempFileName();
        await File.WriteAllTextAsync(plainDraft, $"[{Line},{Equipment}]", TestContext.Current.CancellationToken);
        await using var plain = await ConsoleServer.StartAsync(
            ontologyJson: $"[{Line}]",
            settings: new Dictionary<string, string?>
            {
                ["Saem:Console:Draft"] = plainDraft,
                ["Saem:Console:Confirmations"] = Path.Combine(Path.GetTempPath(), $"confirmations-{Guid.NewGuid():N}.jsonl"),
            });
        using var builder = plain.Client(ConsoleServer.Token("alice", ["ontology-builders"]));

        var (status, body) = await Confirm(builder);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("settlement").ValueKind);
        File.Delete(plainDraft);
    }

    private HttpClient Builder() => _server.Client(ConsoleServer.Token("alice", ["ontology-builders"]));

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Confirm(HttpClient builder)
    {
        var ct = TestContext.Current.CancellationToken;
        var view = await builder.GetFromJsonAsync<JsonElement>("api/catalog/draft", ct);
        using var response = await builder.PostAsJsonAsync(
            "api/catalog/confirm",
            new { @base = view.GetProperty("inForce").GetString(), draft = view.GetProperty("draft").GetString() },
            ct);
        var body = response.Content.Headers.ContentLength == 0 ? default : await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return (response.StatusCode, body);
    }
}
