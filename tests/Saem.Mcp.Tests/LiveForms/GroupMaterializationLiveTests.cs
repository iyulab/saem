using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Saem.Connectors.Postgres;
using Saem.Contracts;
using Saem.Materialization;
using Saem.Ontology;
using Saem.Query;
using Xunit;

namespace Saem.Mcp.Tests.LiveForms;

/// <summary>
/// H2b on the rig (D-22, ADR-0013 by role × group): two groups, each with its own store — a MorphDB project,
/// a secret confined to it, and a Formbase over it. A group's edges are written into its own store and no
/// other: the other group's store holds none of them, and neither group's secret reaches the other's project.
/// The pass over a source reads as the group's account there, so the source's rules for the group decide
/// which edges each store gets (the mes source of the password-login profile, <c>SAEM_RIG_MES</c>).
/// </summary>
public sealed class GroupMaterializationLiveTests : IAsyncLifetime
{
    private const string Canary = "WO-CANARY-PLANT";

    private GroupStoreRig _plant = null!;
    private GroupStoreRig _office = null!;

    public async ValueTask InitializeAsync()
    {
        _plant = await GroupStoreRig.StartAsync("g-plant", TestContext.Current.CancellationToken);
        _office = await GroupStoreRig.StartAsync("g-office", TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _plant.DisposeAsync();
        await _office.DisposeAsync();
    }

    private static readonly EntityIris Iris = new(new Uri("https://rig.saem.test/ontology/"));

    private static MaterializedEdge Edge(string workOrder, string item) =>
        new(Iris.For("WorkOrder", workOrder), "orders_item", Iris.For("Item", item), new SourceRecord("mes", "work_orders", workOrder));

    [Fact]
    public async Task A_groups_edges_land_in_its_own_store_and_nowhere_else()
    {
        var ct = TestContext.Current.CancellationToken;
        var plant = new FormbaseEdgeStore(_plant.Formbase);

        var first = await plant.ReplaceAsync(Of(Edge(Canary, "I-100"), Edge("WO-2", "I-200")), ct);
        var again = await plant.ReplaceAsync(Of(Edge(Canary, "I-100"), Edge("WO-2", "I-200")), ct);

        Assert.Equal(new EdgeWrite(Written: 2, Unchanged: 0, Retired: 0), first with { ReadFrom = null });
        Assert.Equal(new EdgeWrite(Written: 0, Unchanged: 2, Retired: 0), again with { ReadFrom = null });
        Assert.Equal(again.ReadFrom, await plant.AsOfAsync(ct));
        Assert.Null(await new FormbaseEdgeStore(_office.Formbase).AsOfAsync(ct));
        Assert.Equal([Edge("WO-2", "I-200"), Edge(Canary, "I-100")], await plant.ReadAsync(ct).ToListAsync(ct));
        Assert.Empty(await new FormbaseEdgeStore(_office.Formbase).ReadAsync(ct).ToListAsync(ct));
        // D-81 ⑤: compared with what a pass would give now, the store agrees; with a source that moved on, it does not.
        Assert.True((await plant.CompareAsync(Of(Edge(Canary, "I-100"), Edge("WO-2", "I-200")), ct)).Agrees);
        Assert.Equal(new EdgeComparison(Matching: 1, Missing: 0, Differing: 1, Extra: 0, SkippedDocuments: 0), await plant.CompareAsync(Of(Edge(Canary, "I-100"), Edge("WO-2", "I-300")), ct));

        // The edge table in the plant's project has the edge's fields and the engines' own (fb_ · _), nothing a body could fill.
        using var own = _plant.MorphDbAs(_plant.Project);
        var table = await own.GetFromJsonAsync<JsonElement>($"api/schema/tables/{FormbaseEdgeStore.TableName}", ct);
        var columns = table.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).Where(c => !c.StartsWith("fb_", StringComparison.Ordinal) && !c.StartsWith('_')).Order(StringComparer.Ordinal);
        Assert.Equal(["from_iri", "relation", "source_record", "source_system", "source_table", "to_iri"], columns);
    }

    [Fact]
    public async Task A_groups_secret_does_not_reach_the_other_groups_project()
    {
        var ct = TestContext.Current.CancellationToken;
        await new FormbaseEdgeStore(_office.Formbase).ReplaceAsync(Of(Edge("WO-9", "I-900")), ct);

        using var plantAtOffice = _plant.MorphDbAs(_office.Project);
        using var read = await plantAtOffice.GetAsync($"api/schema/tables/{FormbaseEdgeStore.TableName}", ct);
        using var write = await plantAtOffice.PostAsJsonAsync("api/schema/tables", new { name = "planted", columns = new[] { new { name = "x", type = "text" } } }, ct);
        using var officeAtOffice = _office.MorphDbAs(_office.Project);
        using var own = await officeAtOffice.GetAsync($"api/schema/tables/{FormbaseEdgeStore.TableName}", ct);

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    /// <summary>
    /// The mes source shows g_plant every work order and g_office only line 1's: materialized as each group,
    /// the line-2 canary's edge is in the plant's store and not the office's, and its note — a value of the
    /// record, not of the edge — is in neither.
    /// </summary>
    [Fact]
    public async Task A_pass_read_as_each_groups_account_gives_each_store_what_the_source_shows_that_group()
    {
        var ct = TestContext.Current.CancellationToken;
        var mes = new PostgresSource(Environment.GetEnvironmentVariable("SAEM_RIG_MES")
            ?? throw new InvalidOperationException("SAEM_RIG_MES is set by the rig's password-login profile (tests/rig/compose.password-login.yaml)."));
        var ontology = Ontology.Ontology.Create(
        [
            new EntityTypeDeclaration("WorkOrder", "mes", "work_orders", "wo_no", new Dictionary<string, string> { ["item"] = "item_code" }),
            new EntityTypeDeclaration("Item", "erp", "items", "item_code", new Dictionary<string, string>()),
            new RelationDeclaration("orders_item", "WorkOrder", "item", "Item"),
        ]);
        var materializer = new EdgeMaterializer(ontology, Iris);

        var plant = await new FormbaseEdgeStore(_plant.Formbase).ReplaceAsync(
            materializer.MaterializeAsync(As(mes, "g_plant_pw", "rig-plant-local-only"), ct), ct);
        var office = await new FormbaseEdgeStore(_office.Formbase).ReplaceAsync(
            materializer.MaterializeAsync(As(mes, "g_office_pw", "rig-office-local-only"), ct), ct);

        Assert.Equal(new EdgeWrite(Written: 3, Unchanged: 0, Retired: 0), plant with { ReadFrom = null });
        Assert.Equal(new EdgeWrite(Written: 2, Unchanged: 0, Retired: 0), office with { ReadFrom = null });
        var plantEdges = await new FormbaseEdgeStore(_plant.Formbase).ReadAsync(ct).ToListAsync(ct);
        var officeEdges = await new FormbaseEdgeStore(_office.Formbase).ReadAsync(ct).ToListAsync(ct);
        Assert.Contains(Edge("WO-3", "I-100"), plantEdges);
        Assert.Equal([Edge("WO-1", "I-100"), Edge("WO-2", "I-200")], officeEdges);
        foreach (var store in new[] { _plant, _office })
        {
            var stream = await store.Formbase.GetStringAsync("formtypes/edges/documents?limit=1000", ct);
            Assert.DoesNotContain("CANARY", stream, StringComparison.Ordinal);
        }
    }

    private static PersonSessions As(PostgresSource mes, string account, string password) =>
        new(new Dictionary<string, SourceOpener> { ["mes"] = mes.As(account, _ => ValueTask.FromResult(password)) });

    private static async IAsyncEnumerable<MaterializedEdge> Of(params MaterializedEdge[] edges)
    {
        foreach (var edge in edges)
        {
            await Task.Yield();
            yield return edge;
        }
    }
}
