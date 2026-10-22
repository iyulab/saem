using Saem.Authoring;
using Saem.Connectors.Formbase;
using Xunit;

namespace Saem.Mcp.Tests.LiveForms;

/// <summary>
/// The grounding chain through a repeated section: order sheets whose lines are a section of the order,
/// each line naming an item. The lines reach Eyu as rows cut from the orders, an ontology is drafted from
/// them, and a question about a line is answered citing the row itself — the order document and the
/// line's position, the name Formbase gives the row — while a question across the line's relation cites
/// the item's own document.
/// </summary>
public sealed class H2aSectionRowsLiveTests : IAsyncLifetime
{
    private const string Canary = "CANARY-77d1-spindle-bearing";

    private FormsRig _rig = null!;
    private string _order = "";
    private string _item = "";

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _rig = await FormsRig.StartAsync("saem-h2a-rows", ct);

        await _rig.DeclareAsync("items", "items",
        [
            new { name = "item_code", type = "text", nullable = false },
            new { name = "item_name", type = "text", nullable = false },
            new { name = "owner", type = "text", nullable = false },
        ], null, ct);
        await _rig.DeclareAsync("order_lines", "order_lines",
        [
            new { name = "order_no", type = "text", nullable = false },
            new { name = "line_no", type = "text", nullable = false },
            new { name = "item_code", type = "text", nullable = false },
            new { name = "qty", type = "integer", nullable = false },
            new { name = "owner", type = "text", nullable = false },
        ], [new { name = "item", kind = "reference", target = "items", keyField = "item_code" }], ct);
        await _rig.DeclareAsync("orders", "orders",
        [
            new { name = "order_no", type = "text", nullable = false },
            new { name = "customer", type = "text", nullable = false },
            new { name = "owner", type = "text", nullable = false },
        ], [new { name = "lines", kind = "child", target = "order_lines", keyField = "order_no", sectionField = "lines" }], ct);

        _item = await _rig.FileAsync("items", new { item_code = "I-300", item_name = Canary, owner = "alice" }, ct);
        await _rig.FileAsync("items", new { item_code = "I-100", item_name = "Hex bolt M8", owner = "alice" }, ct);
        _order = await _rig.FileAsync("orders", new
        {
            order_no = "SO-51",
            customer = "Hanbit Motors",
            owner = "alice",
            lines = new object[]
            {
                new { line_no = "SO-51-1", item_code = "I-100", qty = 200, owner = "alice" },
                new { line_no = "SO-51-2", item_code = "I-300", qty = 4, owner = "alice" },
            },
        }, ct);
        await _rig.FileAsync("orders", new
        {
            order_no = "SO-52",
            customer = "Daehan Press",
            owner = "alice",
            lines = new object[] { new { line_no = "SO-52-1", item_code = "I-100", qty = 50, owner = "alice" } },
        }, ct);

        foreach (var (type, table) in new[] { ("items", "items"), ("orders", "orders"), ("order_lines", "order_lines") })
        {
            await _rig.ProjectAsync(type, ct);
            await _rig.PolicyAsync(table, "owner = {{user_id}}", ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_rig is not null)
        {
            await _rig.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_line_of_an_order_is_drafted_from_its_rows_and_answered_citing_the_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var sources = new Dictionary<string, SubjectSource>
        {
            ["items"] = new("forms", "items", "item_code", Record: ["fb_doc_id"]),
            ["orders"] = new("forms", "orders", "order_no", Record: ["fb_doc_id"]),
            // A line is a row of its order's document: cited by the document and its position in the section.
            ["order_lines"] = new("forms", "order_lines", "line_no", Record: ["fb_doc_id", "fb_row"]),
        };
        using var model = new LiveModel();
        var connector = new FormbaseConnector(_rig.Formbase);
        var pass = await new DeclarationAuthoring(model.Proposer, connector, connector).DraftAsync(sources, [], 20, ct);
        await LiveModel.ReportAsync("h2a-section-rows", pass.ToReview(), ct);

        // The lines reached Eyu as rows of the orders, each named by the order document and its position.
        Assert.Contains(pass.Records, r => r.Subject?.Value == "order_lines" && r.Id == $"{_order}/1" && r.Fields["item_code"] == "I-300");

        var ontology = Ontology.Ontology.Create(pass.Draft.Declarations);
        var line = Assert.Single(ontology.EntityTypes.Values, t => t.Table == "order_lines");
        var item = Assert.Single(ontology.EntityTypes.Values, t => t.Table == "items");
        var toItem = Assert.Single(ontology.Relations.Values, r => r.From == line.Name && r.To == item.Name);
        // The order's own type does not carry the array its lines are held in: no projected row has it.
        Assert.DoesNotContain("lines", Assert.Single(ontology.EntityTypes.Values, t => t.Table == "orders").Fields.Values);

        await using var server = await TestServer.StartAsync(pass.Draft.ToJson(), _rig.ServerSettings);
        await using var alice = await server.ClientAsync(await FormsRig.TokenAsync("alice", ct), ct);

        var ofLine = (await alice.CallToolAsync("lookup", Call(line.Name, "SO-51-2", [], [Assert.Single(line.Fields, f => f.Value == "qty").Key]), cancellationToken: ct)).StructuredContent!.Value;
        var lineSource = Assert.Single(Assert.Single(ofLine.GetProperty("claims").EnumerateArray()).GetProperty("sources").EnumerateArray());
        Assert.Equal(("order_lines", $"{_order}/1"), (lineSource.GetProperty("table").GetString(), lineSource.GetProperty("record").GetString()));

        var ofItem = (await alice.CallToolAsync("lookup", Call(line.Name, "SO-51-2", [toItem.Name], [Assert.Single(item.Fields, f => f.Value == "item_name").Key]), cancellationToken: ct)).StructuredContent!.Value;
        var claim = Assert.Single(ofItem.GetProperty("claims").EnumerateArray());
        Assert.Equal(Canary, claim.GetProperty("value").GetString());
        Assert.Equal(_item, Assert.Single(claim.GetProperty("sources").EnumerateArray()).GetProperty("record").GetString());
    }

    private static Dictionary<string, object?> Call(string type, string key, string[] path, string[] read) => new()
    {
        ["start"] = new Dictionary<string, object?> { ["type"] = type, ["key"] = key },
        ["path"] = path,
        ["read"] = read,
    };
}
