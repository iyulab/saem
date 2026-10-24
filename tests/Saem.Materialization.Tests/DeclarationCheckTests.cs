using System.Text.Json;
using Saem.Ontology;
using Saem.Query;
using Xunit;

namespace Saem.Materialization.Tests;

/// <summary>
/// SD-03: the declarations checked against the sources as one account — what only the sources can say: whether a
/// declared table is there, whether a key names one row, whether a number key spells an integer, whether the
/// record columns are filled, and how many of a relation's values name an entity the account can read.
/// </summary>
public sealed class DeclarationCheckTests
{
    private static readonly Ontology.Ontology Plant = Ontology.Ontology.Create(
    [
        new EntityTypeDeclaration("WorkOrder", "mes", "work_orders", "wo_no", new Dictionary<string, string> { ["item"] = "item_code" }),
        new EntityTypeDeclaration("Item", "erp", "items", "item_no", new Dictionary<string, string> { ["name"] = "item_name" }) { KeyKind = KeyKind.Number },
        new EntityTypeDeclaration("Inspection", "forms", "inspections", "insp_no", new Dictionary<string, string> { ["order"] = "wo_no" }, Record: ["fb_doc_id"]),
        new RelationDeclaration("orders_item", "WorkOrder", "item", "Item"),
        new RelationDeclaration("inspects", "Inspection", "order", "WorkOrder"),
    ]);

    private static SourceRow Row(string key, params (string Column, object? Value)[] values) =>
        new(key, values.ToDictionary(v => v.Column, v => JsonSerializer.SerializeToElement(v.Value)));

    [Fact]
    public async Task Declarations_the_sources_bear_out_have_no_violation_and_report_their_counts()
    {
        var sessions = new Tables(new()
        {
            ["mes/work_orders"] = [Row("WO-1", ("item_code", "0100")), Row("WO-2", ("item_code", null))],
            ["erp/items"] = [Row("100", ("item_name", "Bracket"))],
            ["forms/inspections"] = [Row("IN-1", ("wo_no", "WO-1"), ("fb_doc_id", "d-1"))],
        });

        var report = await new DeclarationCheck(Plant).RunAsync(sessions, TestContext.Current.CancellationToken);

        Assert.Empty(report.Violations);
        var resolved = Assert.Single(report.Findings, f => f.Relation == "orders_item");
        Assert.Equal("resolved", resolved.Kind);
        Assert.Contains("1 of 1 value(s) of 'item' name a 'Item'", resolved.What, StringComparison.Ordinal);
        Assert.Equal(3, sessions.Closed);
    }

    [Fact]
    public async Task What_the_sources_do_not_bear_out_is_reported_citing_the_records()
    {
        var sessions = new Tables(new()
        {
            ["mes/work_orders"] = [Row("WO-1", ("item_code", "100")), Row("WO-1", ("item_code", "7")), Row("WO-3", ("item_code", "9"))],
            ["erp/items"] = [Row("100", ("item_name", "Bracket")), Row("A-7", ("item_name", "Gasket"))],
            ["forms/inspections"] = [Row("IN-1", ("wo_no", "WO-1"), ("fb_doc_id", null))],
        });

        var report = await new DeclarationCheck(Plant).RunAsync(sessions, TestContext.Current.CancellationToken);

        var violations = report.Violations.ToDictionary(f => (f.Type, f.Relation, f.Kind));
        Assert.Equal(["WO-1"], violations[("WorkOrder", null, "duplicateKey")].Records);
        Assert.Equal(["A-7"], violations[("Item", null, "notInteger")].Records);
        Assert.Equal(["IN-1"], violations[("Inspection", null, "emptyRecord")].Records);
        var unresolved = violations[("WorkOrder", "orders_item", "unresolved")];
        Assert.Equal(2, unresolved.Count);
        Assert.Equal(["WO-1", "WO-3"], unresolved.Records);
        Assert.Equal(4, report.Violations.Count);
    }

    /// <summary>A table the account cannot read is a finding about that table; the other tables are still checked.</summary>
    [Fact]
    public async Task A_table_that_cannot_be_read_is_a_finding_and_the_rest_are_still_checked()
    {
        var sessions = new Tables(new()
        {
            ["erp/items"] = [Row("100", ("item_name", "Bracket"))],
            ["forms/inspections"] = [],
        }, failing: "mes/work_orders");

        var report = await new DeclarationCheck(Plant).RunAsync(sessions, TestContext.Current.CancellationToken);

        var unreadable = Assert.Single(report.Violations);
        Assert.Equal(("WorkOrder", "unreadable"), (unreadable.Type, unreadable.Kind));
        Assert.Contains("'mes'.'work_orders' could not be read", unreadable.What, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Findings, f => f.Relation == "orders_item");
        Assert.Contains(report.Findings, f => f.Type == "Item" && f.Kind == "read");
    }

    /// <summary>A session that failed a read is not reused: the system's next table is read over a fresh session.</summary>
    [Fact]
    public async Task After_a_table_cannot_be_read_its_system_is_read_over_a_fresh_session()
    {
        var sessions = new Tables(new()
        {
            ["mes/work_orders"] = [Row("WO-1", ("item_code", "100"))],
            ["erp/items"] = [Row("100", ("item_name", "Bracket"))],
        }, failing: "forms/inspections");
        var withSecondForms = Ontology.Ontology.Create(
        [
            .. Plant.EntityTypes.Values, .. Plant.Relations.Values,
            new EntityTypeDeclaration("Audit", "forms", "audits", "audit_no", new Dictionary<string, string>()),
        ]);

        await new DeclarationCheck(withSecondForms).RunAsync(sessions, TestContext.Current.CancellationToken);

        Assert.Equal(2, sessions.Opened.Count(s => s == "forms"));
    }

    /// <summary>Sessions over fixed tables, keyed by <c>system/table</c>; a table not given, or the failing one, cannot be read.</summary>
    private sealed class Tables(Dictionary<string, List<SourceRow>> tables, string? failing = null) : ISourceSessions
    {
        public int Closed { get; private set; }

        public List<string> Opened { get; } = [];

        private IEnumerable<SourceRow> Scan(string table, IReadOnlyList<string> columns) =>
            table == failing || !tables.TryGetValue(table, out var rows)
                ? throw new InvalidOperationException($"relation \"{table}\" does not exist")
                : rows.Select(r => new SourceRow(r.Key, columns.ToDictionary(c => c, c => r.Values.TryGetValue(c, out var v) ? v : JsonSerializer.SerializeToElement<object?>(null))));

        public ValueTask<ISourceSession> OpenAsync(string system, CancellationToken cancellationToken = default)
        {
            Opened.Add(system);
            return ValueTask.FromResult<ISourceSession>(new Session(this, system));
        }

        private sealed class Session(Tables owner, string system) : ISourceSession
        {
            public ValueTask<IReadOnlyList<SourceRow>> ReadAsync(string table, string keyColumn, string key, IReadOnlyList<string> columns, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("A check scans.");

            public ValueTask<IReadOnlyList<SourceRow>> FindAsync(string table, string keyColumn, string column, string value, IReadOnlyList<string> columns, int limit, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("A check scans.");

            public IAsyncEnumerable<SourceRow> ScanAsync(string table, string keyColumn, IReadOnlyList<string> columns, CancellationToken cancellationToken = default) =>
                owner.Scan($"{system}/{table}", columns).ToAsyncEnumerable();

            public ValueTask DisposeAsync()
            {
                owner.Closed++;
                return ValueTask.CompletedTask;
            }
        }
    }
}
