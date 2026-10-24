using System.Text.Json;
using Saem.Contracts;
using Saem.Ontology;
using Saem.Query;
using Xunit;

namespace Saem.Materialization.Tests;

/// <summary>
/// The edges of a group: one per row of the leaving type's table whose relation field holds a key, read
/// through the group's sessions — so a row the group may not see leaves no edge — and named the same way
/// on every pass.
/// </summary>
public sealed class EdgeMaterializerTests
{
    private static readonly EntityIris Iris = new(new Uri("https://example.org/ontology/"));

    private static readonly Ontology.Ontology Plant = Ontology.Ontology.Create(
    [
        new EntityTypeDeclaration("WorkOrder", "mes", "work_orders", "wo_no", new Dictionary<string, string> { ["item"] = "item_code", ["quantity"] = "qty" }),
        new EntityTypeDeclaration("Item", "erp", "items", "item_code", new Dictionary<string, string> { ["name"] = "item_name" }),
        new EntityTypeDeclaration("Inspection", "forms", "inspections", "insp_no", new Dictionary<string, string> { ["order"] = "wo_no" }, Record: ["fb_doc_id"]),
        new RelationDeclaration("orders_item", "WorkOrder", "item", "Item"),
        new RelationDeclaration("inspects", "Inspection", "order", "WorkOrder"),
    ]);

    [Fact]
    public async Task Each_row_whose_relation_field_holds_a_key_is_an_edge_cited_by_its_record()
    {
        var sessions = new Sessions(new()
        {
            ["mes/work_orders"] = [Row("WO-1", ("item_code", "I-100"), ("qty", 5)), Row("WO-2", ("item_code", null), ("qty", 1))],
            ["forms/inspections"] = [Row("IN-1", ("wo_no", "WO-1"), ("fb_doc_id", "d-7"))],
        });

        var edges = await new EdgeMaterializer(Plant, Iris).MaterializeAsync(sessions, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            new MaterializedEdge("https://example.org/ontology/Inspection/IN-1", "inspects", "https://example.org/ontology/WorkOrder/WO-1", new SourceRecord("forms", "inspections", "d-7")),
            new MaterializedEdge("https://example.org/ontology/WorkOrder/WO-1", "orders_item", "https://example.org/ontology/Item/I-100", new SourceRecord("mes", "work_orders", "WO-1")),
        ], edges);
    }

    /// <summary>D-82 ② · defect #12: an edge names a number-keyed entity by the integer its reference spells — the IRI a lookup asks by.</summary>
    [Fact]
    public async Task An_edge_to_a_number_keyed_entity_names_it_by_the_integer()
    {
        var numbered = Ontology.Ontology.Create(
        [
            new EntityTypeDeclaration("WorkOrder", "mes", "work_orders", "wo_no", new Dictionary<string, string> { ["item"] = "item_code" }),
            new EntityTypeDeclaration("Item", "erp", "items", "item_code", new Dictionary<string, string> { ["name"] = "item_name" }) { KeyKind = KeyKind.Number },
            new RelationDeclaration("orders_item", "WorkOrder", "item", "Item"),
        ]);
        var sessions = new Sessions(new() { ["mes/work_orders"] = [Row("WO-1", ("item_code", "000100"))] });

        var edge = Assert.Single(await new EdgeMaterializer(numbered, Iris).MaterializeAsync(sessions, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));

        Assert.Equal("https://example.org/ontology/Item/100", edge.To);
    }

    [Fact]
    public async Task Only_the_relation_field_and_the_record_columns_are_read_and_every_session_is_closed()
    {
        var sessions = new Sessions(new()
        {
            ["mes/work_orders"] = [Row("WO-1", ("item_code", "I-100"), ("qty", 5))],
            ["forms/inspections"] = [],
        });

        await new EdgeMaterializer(Plant, Iris).MaterializeAsync(sessions, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["forms inspections insp_no [wo_no,fb_doc_id]", "mes work_orders wo_no [item_code]"], sessions.Scans);
        Assert.Equal(2, sessions.Closed);
    }

    [Fact]
    public async Task A_row_the_group_may_not_see_leaves_no_edge()
    {
        var office = new Sessions(new() { ["mes/work_orders"] = [], ["forms/inspections"] = [] });

        Assert.Empty(await new EdgeMaterializer(Plant, Iris).MaterializeAsync(office, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void An_entity_is_named_the_same_way_on_every_pass_and_its_parts_are_escaped()
    {
        Assert.Equal(Iris.For("WorkOrder", "WO-1"), new EntityIris(new Uri("https://example.org/ontology")).For("WorkOrder", "WO-1"));
        Assert.Equal("https://example.org/ontology/Work%20Order/A%2FB%3F1", Iris.For("Work Order", "A/B?1"));
        Assert.Throws<ArgumentException>(() => new EntityIris(new Uri("relative/", UriKind.Relative)));
    }

    /// <summary>An IRI reads back to the key it was minted from, and only under its own base and type.</summary>
    [Fact]
    public void An_iri_reads_back_as_its_key_under_its_own_type_only()
    {
        Assert.Equal("A/B?1", Iris.KeyOf("Work Order", Iris.For("Work Order", "A/B?1")));
        Assert.Null(Iris.KeyOf("Item", Iris.For("WorkOrder", "WO-1")));
        Assert.Null(Iris.KeyOf("WorkOrder", "https://elsewhere.org/ontology/WorkOrder/WO-1"));
        Assert.Null(Iris.KeyOf("WorkOrder", Iris.For("WorkOrder", "")));
    }

    private static SourceRow Row(string key, params (string Column, object? Value)[] values) =>
        new(key, values.ToDictionary(v => v.Column, v => JsonSerializer.SerializeToElement(v.Value)));

    /// <summary>Sessions over fixed tables, keyed by <c>system/table</c>, recording what each scan asked for.</summary>
    private sealed class Sessions(Dictionary<string, List<SourceRow>> tables) : ISourceSessions
    {
        public List<string> Scans { get; } = [];

        public int Closed { get; private set; }

        public ValueTask<ISourceSession> OpenAsync(string system, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ISourceSession>(new Session(this, system));

        private sealed class Session(Sessions owner, string system) : ISourceSession
        {
            public ValueTask<IReadOnlyList<SourceRow>> ReadAsync(string table, string keyColumn, string key, IReadOnlyList<string> columns, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("Materialization scans; it never reads by key.");

            public ValueTask<IReadOnlyList<SourceRow>> FindAsync(string table, string keyColumn, string column, string value, IReadOnlyList<string> columns, int limit, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("Materialization scans; it never finds by a field.");

            public IAsyncEnumerable<SourceRow> ScanAsync(string table, string keyColumn, IReadOnlyList<string> columns, CancellationToken cancellationToken = default)
            {
                owner.Scans.Add($"{system} {table} {keyColumn} [{string.Join(",", columns)}]");
                return owner.Rows($"{system}/{table}").ToAsyncEnumerable();
            }

            public ValueTask DisposeAsync()
            {
                owner.Closed++;
                return ValueTask.CompletedTask;
            }
        }

        private List<SourceRow> Rows(string table) => tables.GetValueOrDefault(table) ?? [];
    }
}
