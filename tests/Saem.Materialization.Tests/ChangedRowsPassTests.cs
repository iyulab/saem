using System.Text.Json;
using Saem.Ontology;
using Saem.Query;
using Xunit;

namespace Saem.Materialization.Tests;

/// <summary>
/// SD-08: a pass narrowed to the rows changed since the last one leaves the store holding what a whole pass would —
/// for changes that delete nothing — having read only those rows. The source's change times here are text that
/// sorts as the times do, compared by the stand-in as a source compares its own values.
/// </summary>
public sealed class ChangedRowsPassTests
{
    private static readonly EntityIris Iris = new(new Uri("https://example.org/ontology/"));

    private static readonly Ontology.Ontology Plant = Ontology.Ontology.Create(
    [
        new EntityTypeDeclaration("WorkOrder", "mes", "work_orders", "wo_no", new Dictionary<string, string> { ["item"] = "item_code" }) { ChangedAt = "updated_at" },
        new EntityTypeDeclaration("Item", "erp", "items", "item_code", new Dictionary<string, string> { ["name"] = "item_name" }),
        new RelationDeclaration("orders_item", "WorkOrder", "item", "Item"),
    ]);

    private static SourceRow Order(string key, string? item, string? at) => EdgeMaterializerTests.Row(key, ("item_code", item), ("updated_at", at));

    /// <summary>A pass as <c>saem-materialize</c> makes one: whole, or <c>--changed</c>.</summary>
    private static Task<EdgeWrite> PassAsync(Ontology.Ontology ontology, ChangingTable sessions, FormbaseEdgeStore store, bool changedOnly) =>
        Cli.MaterializeRun.RunAsync(
            ontology, new Dictionary<string, SourceOpener> { ["mes"] = ct => sessions.OpenAsync("mes", ct) }, "g-plant", Iris, store, null, TextWriter.Null,
            TestContext.Current.CancellationToken, changedOnly);

    [Fact]
    public async Task A_pass_over_changed_rows_reads_only_them_and_leaves_what_a_whole_pass_would()
    {
        var table = new ChangingTable(
        [
            Order("WO-1", "I-1", "t1"), Order("WO-2", "I-2", "t2"), Order("WO-3", null, "t3"), Order("WO-4", "I-4", "t4"), Order("WO-5", "I-5", "t5"),
        ]);
        var formbase = new FakeFormbase();
        var store = new FormbaseEdgeStore(formbase.Client);
        await PassAsync(Plant, table, store, changedOnly: false);
        Assert.Equal(new Dictionary<string, string> { ["orders_item"] = "t5" }, await store.WatermarksAsync(TestContext.Current.CancellationToken));

        // WO-1 now makes I-9, WO-2 makes nothing, WO-6 is new.
        table.Rows[0] = Order("WO-1", "I-9", "t7");
        table.Rows[1] = Order("WO-2", null, "t6");
        table.Rows.Add(Order("WO-6", "I-6", "t8"));
        table.Scans.Clear();
        var write = await PassAsync(Plant, table, store, changedOnly: true);

        // At or after t5: WO-5 again (the boundary), and the three changed.
        Assert.Equal(["mes work_orders updated_at>=t5"], table.Scans);
        Assert.Equal((4, 1, 2, 1, 1), (write.RowsRead, write.Unchanged, write.Written, write.Retired, write.ReadInPart));
        var whole = new FormbaseEdgeStore(new FakeFormbase().Client);
        await PassAsync(Plant, table, whole, changedOnly: false);
        Assert.Equal(
            await whole.ReadAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken),
            await store.ReadAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal("t8", (await store.WatermarksAsync(TestContext.Current.CancellationToken))["orders_item"]);
    }

    /// <summary>What a narrowed pass cannot see — a row deleted — stands until a whole pass, which retires it.</summary>
    [Fact]
    public async Task A_row_deleted_is_seen_only_by_a_whole_pass()
    {
        var table = new ChangingTable([Order("WO-1", "I-1", "t1"), Order("WO-2", "I-2", "t2")]);
        var store = new FormbaseEdgeStore(new FakeFormbase().Client);
        await PassAsync(Plant, table, store, changedOnly: false);
        table.Rows.RemoveAt(0);

        var narrowed = await PassAsync(Plant, table, store, changedOnly: true);
        var whole = await PassAsync(Plant, table, store, changedOnly: false);

        Assert.Equal(0, narrowed.Retired);
        Assert.Equal(1, whole.Retired);
        Assert.Equal(["https://example.org/ontology/WorkOrder/WO-2"], (await store.ReadAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken)).Select(e => e.From));
    }

    /// <summary>A type that declares no change column, or a first pass with no latest change filed, is read whole.</summary>
    [Fact]
    public async Task Without_a_change_column_or_a_filed_latest_change_a_pass_reads_the_whole_table()
    {
        var unhinted = Ontology.Ontology.Create([.. Plant.EntityTypes.Values.Select(t => t with { ChangedAt = null }), .. Plant.Relations.Values]);
        var table = new ChangingTable([Order("WO-1", "I-1", "t1")]);
        var store = new FormbaseEdgeStore(new FakeFormbase().Client);

        var first = await PassAsync(Plant, table, store, changedOnly: true);
        var plain = await PassAsync(unhinted, table, store, changedOnly: true);

        Assert.Equal(0, first.ReadInPart);
        Assert.Equal(["mes work_orders whole", "mes work_orders whole"], table.Scans);
        Assert.Empty(await store.WatermarksAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, plain.ReadInPart);
    }

    /// <summary>One source table of work orders that can tell its changed rows, as a session that is <see cref="IChangedRows"/>.</summary>
    private sealed class ChangingTable(List<SourceRow> rows) : ISourceSessions
    {
        public List<SourceRow> Rows { get; } = rows;

        public List<string> Scans { get; } = [];

        public ValueTask<ISourceSession> OpenAsync(string system, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ISourceSession>(new Session(this));

        private sealed class Session(ChangingTable owner) : ISourceSession, IChangedRows
        {
            public ValueTask<IReadOnlyList<SourceRow>> ReadAsync(string table, string keyColumn, string key, IReadOnlyList<string> columns, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("Materialization scans.");

            public ValueTask<IReadOnlyList<SourceRow>> FindAsync(string table, string keyColumn, string column, string value, IReadOnlyList<string> columns, int limit, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("Materialization scans.");

            public IAsyncEnumerable<SourceRow> ScanAsync(string table, string keyColumn, IReadOnlyList<string> columns, CancellationToken cancellationToken = default)
            {
                owner.Scans.Add($"mes {table} whole");
                return Ordered(owner.Rows).ToAsyncEnumerable();
            }

            public ValueTask<string?> LatestChangeAsync(string table, string column, CancellationToken cancellationToken = default) =>
                ValueTask.FromResult(owner.Rows.Select(r => At(r, column)).OfType<string>().Order(StringComparer.Ordinal).LastOrDefault());

            public IAsyncEnumerable<SourceRow> ScanChangedAsync(string table, string keyColumn, IReadOnlyList<string> columns, string column, string since, CancellationToken cancellationToken = default)
            {
                owner.Scans.Add($"mes {table} {column}>={since}");
                return Ordered(owner.Rows.Where(r => At(r, column) is { } at && string.CompareOrdinal(at, since) >= 0)).ToAsyncEnumerable();
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            private static IEnumerable<SourceRow> Ordered(IEnumerable<SourceRow> rows) => rows.OrderBy(r => r.Key, StringComparer.Ordinal);

            private static string? At(SourceRow row, string column) =>
                row.Values[column].ValueKind == JsonValueKind.String ? row.Values[column].GetString() : null;
        }
    }
}
