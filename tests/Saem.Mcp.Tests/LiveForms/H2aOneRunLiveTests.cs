using Saem.Authoring;
using Saem.Connectors.Formbase;
using Saem.Ontology;
using Xunit;

namespace Saem.Mcp.Tests.LiveForms;

/// <summary>
/// The grounding chain in one run: forms filed in Formbase, an ontology drafted from them by Eyu
/// against a real model, that draft taken as the ontology saem answers in, and a question asked across
/// a drafted relation as a person. The answer's source is the form document the asked record was
/// projected from — not the record's key — and MorphDB's row-level security still decides who is
/// answered. Nothing is declared by hand: the types, their tables and the relation come from the draft,
/// and the review a person would read is written out.
/// </summary>
public sealed class H2aOneRunLiveTests : IAsyncLifetime
{
    private const string Canary = "CANARY-5e02-hex-bolt-from-daehan";

    private FormsRig _rig = null!;
    private readonly Dictionary<string, string> _documents = new(StringComparer.Ordinal);

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _rig = await FormsRig.StartAsync("saem-h2a-run", ct);

        await _rig.DeclareAsync("items", "items",
        [
            new { name = "item_code", type = "text", nullable = false },
            new { name = "item_name", type = "text", nullable = false },
            new { name = "owner", type = "text", nullable = false },
        ], null, ct);
        await _rig.DeclareAsync("work_orders", "work_orders",
        [
            new { name = "wo_no", type = "text", nullable = false },
            new { name = "item_code", type = "text", nullable = false },
            new { name = "qty", type = "integer", nullable = false },
            new { name = "owner", type = "text", nullable = false },
        ], [new { name = "item", kind = "reference", target = "items", keyField = "item_code" }], ct);

        foreach (var (code, name) in new[] { ("I-100", Canary), ("I-200", "Hex nut M8"), ("I-300", "Ball bearing 6204") })
        {
            _documents[code] = await _rig.FileAsync("items", new { item_code = code, item_name = name, owner = "alice" }, ct);
        }

        foreach (var (wo, item, qty) in new[] { ("WO-2401", "I-100", 500), ("WO-2402", "I-300", 40), ("WO-2403", "I-200", 500), ("WO-2404", "I-300", 12) })
        {
            await _rig.FileAsync("work_orders", new { wo_no = wo, item_code = item, qty, owner = "alice" }, ct);
        }

        foreach (var type in new[] { "items", "work_orders" })
        {
            await _rig.ProjectAsync(type, ct);
            await _rig.PolicyAsync(type, "owner = {{user_id}}", ct);
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
    public async Task An_ontology_drafted_from_the_forms_answers_across_its_relation_citing_the_form_document()
    {
        var ct = TestContext.Current.CancellationToken;

        // Each form type stands on its projected table, keyed by the record's own key and cited by the
        // document a row was projected from.
        var sources = new Dictionary<string, SubjectSource>
        {
            ["items"] = new("forms", "items", "item_code", Record: ["fb_doc_id"]),
            ["work_orders"] = new("forms", "work_orders", "wo_no", Record: ["fb_doc_id"]),
        };
        using var model = new LiveModel();
        var connector = new FormbaseConnector(_rig.Formbase);
        var pass = await new DeclarationAuthoring(model.Proposer, connector, connector).DraftAsync(sources, [], 20, ct);
        await LiveModel.ReportAsync("h2a-one-run", pass.ToReview(), ct);

        // The draft as the reviewer would accept it, unchanged.
        var ontology = Ontology.Ontology.Create(pass.Draft.Declarations);
        var item = Assert.Single(ontology.EntityTypes.Values, t => t.Table == "items");
        var workOrder = Assert.Single(ontology.EntityTypes.Values, t => t.Table == "work_orders");
        var ordersItem = Assert.Single(ontology.Relations.Values, r => r.From == workOrder.Name && r.To == item.Name);
        var name = Assert.Single(item.Fields, f => f.Value == "item_name").Key;

        await using var server = await TestServer.StartAsync(pass.Draft.ToJson(), _rig.ServerSettings);
        var call = new Dictionary<string, object?>
        {
            ["start"] = new Dictionary<string, object?> { ["type"] = workOrder.Name, ["key"] = "WO-2401" },
            ["path"] = new[] { ordersItem.Name },
            ["read"] = new[] { name },
        };

        await using var alice = await server.ClientAsync(await FormsRig.TokenAsync("alice", ct), ct);
        var forAlice = (await alice.CallToolAsync("lookup", call, cancellationToken: ct)).StructuredContent!.Value;
        await using var bob = await server.ClientAsync(await FormsRig.TokenAsync("bob", ct), ct);
        var forBob = (await bob.CallToolAsync("lookup", call, cancellationToken: ct)).StructuredContent!.Value;

        var claim = Assert.Single(forAlice.GetProperty("claims").EnumerateArray());
        Assert.Equal(Canary, claim.GetProperty("value").GetString());
        Assert.Equal("I-100", claim.GetProperty("entity").GetProperty("key").GetString());
        var source = Assert.Single(claim.GetProperty("sources").EnumerateArray());
        Assert.Equal(("forms", "items", _documents["I-100"]),
            (source.GetProperty("system").GetString(), source.GetProperty("table").GetString(), source.GetProperty("record").GetString()));

        Assert.Equal("refused", forBob.GetProperty("kind").GetString());
        Assert.DoesNotContain(Canary, forBob.GetRawText(), StringComparison.Ordinal);
    }
}
