using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Saem.Contracts;
using Xunit;

namespace Saem.Materialization.Tests;

/// <summary>
/// The edge store against a stand-in Formbase that keeps documents by record key and projects them the way
/// Formbase documents it (each record once, its latest document; a retired record gone). The live
/// counterpart writes into a group's Formbase on the rig.
/// </summary>
public sealed class FormbaseEdgeStoreTests
{
    private static readonly SourceRecord WorkOrderRow = new("mes", "work_orders", "WO-1");

    private static MaterializedEdge Edge(string from, string to, string relation = "orders_item") =>
        new($"https://example.org/WorkOrder/{from}", relation, $"https://example.org/Item/{to}", WorkOrderRow with { Record = from });

    [Fact]
    public async Task A_first_pass_declares_the_edge_shape_files_every_edge_and_projects_them()
    {
        var formbase = new FakeFormbase();

        var write = await new FormbaseEdgeStore(formbase.Client).ReplaceAsync(Of(Edge("WO-1", "I-1"), Edge("WO-2", "I-2")), TestContext.Current.CancellationToken);

        Assert.Equal(new EdgeWrite(Written: 2, Unchanged: 0, Retired: 0), write with { ReadFrom = null });
        var declaration = Assert.IsType<JsonObject>(formbase.Declaration);
        Assert.Equal("saem_edges", (string?)declaration["tableName"]);
        Assert.Equal(
            ["from_iri", "relation", "to_iri", "source_system", "source_table", "source_record"],
            declaration["fields"]!.AsArray().Select(f => (string?)f!["name"]));
        Assert.All(declaration["fields"]!.AsArray(), f => Assert.Equal(("text", false), ((string?)f!["type"], (bool)f["nullable"]!)));
        Assert.Equal([Edge("WO-1", "I-1"), Edge("WO-2", "I-2")], await new FormbaseEdgeStore(formbase.Client).ReadAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A document carries the edge's six fields and nothing else — there is nowhere a record's body could go.</summary>
    [Fact]
    public async Task A_filed_edge_carries_only_the_edge_fields()
    {
        var formbase = new FakeFormbase();

        await new FormbaseEdgeStore(formbase.Client).ReplaceAsync(Of(Edge("WO-1", "I-1")), TestContext.Current.CancellationToken);

        var document = Assert.Single(formbase.Stream).Body!;
        Assert.Equal(["from_iri", "relation", "to_iri", "source_system", "source_table", "source_record"], document.Select(p => p.Key));
        Assert.Equal("orders_item https://example.org/WorkOrder/WO-1", Assert.Single(formbase.Stream).Key);
    }

    [Fact]
    public async Task A_pass_made_again_over_the_same_edges_sends_nothing()
    {
        var formbase = new FakeFormbase();
        var store = new FormbaseEdgeStore(formbase.Client);
        await store.ReplaceAsync(Of(Edge("WO-1", "I-1"), Edge("WO-2", "I-2")), TestContext.Current.CancellationToken);

        var again = await store.ReplaceAsync(Of(Edge("WO-2", "I-2"), Edge("WO-1", "I-1")), TestContext.Current.CancellationToken);

        Assert.Equal(new EdgeWrite(Written: 0, Unchanged: 2, Retired: 0), again with { ReadFrom = null });
        Assert.Equal(2, formbase.Stream.Count);
    }

    [Fact]
    public async Task An_entity_that_now_leads_elsewhere_is_corrected_and_one_no_longer_given_is_retired()
    {
        var formbase = new FakeFormbase();
        var store = new FormbaseEdgeStore(formbase.Client);
        await store.ReplaceAsync(Of(Edge("WO-1", "I-1"), Edge("WO-2", "I-2")), TestContext.Current.CancellationToken);

        var next = await store.ReplaceAsync(Of(Edge("WO-1", "I-9")), TestContext.Current.CancellationToken);

        Assert.Equal(new EdgeWrite(Written: 1, Unchanged: 0, Retired: 1), next with { ReadFrom = null });
        Assert.Equal([Edge("WO-1", "I-9")], await store.ReadAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Walked backwards, a relation gives the edges that lead to one entity — asked of the store by its fields.</summary>
    [Fact]
    public async Task The_edges_leading_to_an_entity_are_those_of_the_relation_whose_target_it_is()
    {
        var formbase = new FakeFormbase();
        var store = new FormbaseEdgeStore(formbase.Client);
        await store.ReplaceAsync(Of(Edge("WO-1", "I-1"), Edge("WO-2", "I-2"), Edge("WO-3", "I-1"), Edge("WO-4", "I-1", relation: "replaces")), TestContext.Current.CancellationToken);

        var leading = await store.LeadingToAsync("orders_item", "https://example.org/Item/I-1", TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([Edge("WO-1", "I-1"), Edge("WO-3", "I-1")], leading);
        Assert.Empty(await new FormbaseEdgeStore(new FakeFormbase().Client).LeadingToAsync("orders_item", "https://example.org/Item/I-1", TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_form_type_declared_with_another_shape_is_not_written_over()
    {
        var formbase = new FakeFormbase
        {
            Declaration = new JsonObject
            {
                ["tableName"] = "saem_edges",
                ["declarationVersion"] = 1,
                ["fields"] = new JsonArray(new JsonObject { ["name"] = "from_iri", ["type"] = "text", ["nullable"] = false }, new JsonObject { ["name"] = "body", ["type"] = "jsonb", ["nullable"] = true }),
            },
        };

        await Assert.ThrowsAsync<EdgeStoreException>(() => new FormbaseEdgeStore(formbase.Client).ReplaceAsync(Of(Edge("WO-1", "I-1")), TestContext.Current.CancellationToken));
        Assert.Empty(formbase.Stream);
    }

    [Fact]
    public async Task Two_edges_of_one_relation_leaving_one_entity_are_refused()
    {
        var formbase = new FakeFormbase();

        await Assert.ThrowsAsync<EdgeStoreException>(() => new FormbaseEdgeStore(formbase.Client).ReplaceAsync(Of(Edge("WO-1", "I-1"), Edge("WO-1", "I-2")), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_namespace_is_sent_on_every_request()
    {
        var formbase = new FakeFormbase();

        await new FormbaseEdgeStore(formbase.Client, "g-plant").ReplaceAsync(Of(Edge("WO-1", "I-1")), TestContext.Current.CancellationToken);

        Assert.All(formbase.Requests, r => Assert.Equal("g-plant", r));
    }

    private static async IAsyncEnumerable<MaterializedEdge> Of(params MaterializedEdge[] edges)
    {
        foreach (var edge in edges)
        {
            await Task.Yield();
            yield return edge;
        }
    }

    [Fact]
    public async Task A_pass_files_when_it_began_reading_once_its_edges_are_in()
    {
        var formbase = new FakeFormbase();
        var started = new DateTimeOffset(2026, 10, 25, 3, 0, 0, TimeSpan.Zero);
        var store = new FormbaseEdgeStore(formbase.Client, time: new FixedClock(started));
        Assert.Null(await store.AsOfAsync(TestContext.Current.CancellationToken));

        var write = await store.ReplaceAsync(Of(Edge("WO-1", "I-1")), TestContext.Current.CancellationToken);

        Assert.Equal(started, write.ReadFrom);
        Assert.Equal(started, await store.AsOfAsync(TestContext.Current.CancellationToken));
        var pass = Assert.Single(formbase.Passes.Stream);
        Assert.Equal("latest", pass.Key);
        Assert.Equal(["read_from"], pass.Body!.Select(p => p.Key));
        Assert.Equal("saem_edge_passes", (string?)formbase.Passes.Declaration!["tableName"]);
    }

    [Fact]
    public async Task A_pass_that_fails_leaves_the_time_of_the_last_one_that_did_not()
    {
        var formbase = new FakeFormbase();
        var first = new DateTimeOffset(2026, 10, 25, 3, 0, 0, TimeSpan.Zero);
        await new FormbaseEdgeStore(formbase.Client, time: new FixedClock(first)).ReplaceAsync(Of(Edge("WO-1", "I-1")), TestContext.Current.CancellationToken);

        var failing = new FormbaseEdgeStore(formbase.Client, time: new FixedClock(first.AddHours(1)));
        await Assert.ThrowsAsync<EdgeStoreException>(() => failing.ReplaceAsync(Of(Edge("WO-1", "I-1"), Edge("WO-1", "I-2")), TestContext.Current.CancellationToken));

        Assert.Equal(first, await failing.AsOfAsync(TestContext.Current.CancellationToken));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
