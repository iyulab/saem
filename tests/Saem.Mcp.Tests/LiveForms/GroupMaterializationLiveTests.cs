using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Saem.Contracts;
using Saem.Materialization;
using Xunit;

namespace Saem.Mcp.Tests.LiveForms;

/// <summary>
/// H2b on the rig (D-22, ADR-0013 by role × group): two groups, each with its own store — a MorphDB project,
/// a secret confined to it, and a Formbase over it. A group's edges are written into its own store and no
/// other: the other group's store holds none of them, and neither group's secret reaches the other's project.
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

        Assert.Equal(new EdgeWrite(Written: 2, Unchanged: 0, Retired: 0), first);
        Assert.Equal(new EdgeWrite(Written: 0, Unchanged: 2, Retired: 0), again);
        Assert.Equal([Edge("WO-2", "I-200"), Edge(Canary, "I-100")], await plant.ReadAsync(ct).ToListAsync(ct));
        Assert.Empty(await new FormbaseEdgeStore(_office.Formbase).ReadAsync(ct).ToListAsync(ct));

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

    private static async IAsyncEnumerable<MaterializedEdge> Of(params MaterializedEdge[] edges)
    {
        foreach (var edge in edges)
        {
            await Task.Yield();
            yield return edge;
        }
    }
}
