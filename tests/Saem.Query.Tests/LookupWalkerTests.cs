using System.Text.Json;
using Saem.Contracts;
using Saem.Ontology;
using Xunit;

namespace Saem.Query.Tests;

/// <summary>
/// The walk over two systems, with the sources replaced by tables in memory and each person's view
/// of them decided the way a source decides it — rows the person may not see are simply not there.
/// A work order in the MES names an item; the ERP holds the item and its supplier. Supplier S-2's
/// name is a canary only alice may see.
/// </summary>
public class LookupWalkerTests
{
    private const string Canary = "CANARY-91c4-supplier-two";

    private static readonly Declaration[] Rig =
    [
        new EntityTypeDeclaration("WorkOrder", "mes", "work_orders", "wo_no", new Dictionary<string, string> { ["item"] = "item_code", ["quantity"] = "qty" }),
        new EntityTypeDeclaration("Item", "erp", "visible_items", "item_code", new Dictionary<string, string> { ["name"] = "item_name", ["supplier"] = "supplier_id" }),
        new EntityTypeDeclaration("Supplier", "erp", "visible_suppliers", "supplier_id", new Dictionary<string, string> { ["name"] = "supplier_name" }),
        // Form documents projected into rows: keyed by what each one is, cited by the document read.
        new EntityTypeDeclaration("Inspection", "forms", "inspections", "inspection_no", new Dictionary<string, string> { ["finding"] = "finding" }, ["fb_doc_id"]),
        new EntityTypeDeclaration("Defect", "forms", "inspection_defects", "defect_no", new Dictionary<string, string> { ["part"] = "part" }, ["fb_doc_id", "fb_row"]),
        new RelationDeclaration("orders_item", "WorkOrder", "item", "Item"),
        new RelationDeclaration("supplied_by", "Item", "supplier", "Supplier"),
    ];

    private static readonly LookupWalker Walker = new(new FixedCatalog(Ontology.Ontology.Create(Rig)));

    // Every row of the sources, and who may see it.
    private static readonly (string System, string Table, string Key, Dictionary<string, object?> Values, string[] SeenBy)[] Rows =
    [
        ("mes", "work_orders", "WO-1", new() { ["item_code"] = "I-100", ["qty"] = 50 }, ["alice", "bob"]),
        ("mes", "work_orders", "WO-2", new() { ["item_code"] = "I-200", ["qty"] = 20 }, ["alice", "bob"]),
        ("mes", "work_orders", "WO-3", new() { ["item_code"] = "I-100", ["qty"] = 10 }, ["alice"]),
        ("mes", "work_orders", "WO-4", new() { ["item_code"] = null, ["qty"] = 5 }, ["alice"]),
        ("erp", "visible_items", "I-100", new() { ["item_name"] = "Bracket", ["supplier_id"] = "S-1" }, ["alice", "bob"]),
        ("erp", "visible_items", "I-200", new() { ["item_name"] = "Gasket", ["supplier_id"] = "S-2" }, ["alice", "bob"]),
        ("erp", "visible_suppliers", "S-1", new() { ["supplier_name"] = "Hanbit Parts" }, ["alice", "bob"]),
        ("erp", "visible_suppliers", "S-2", new() { ["supplier_name"] = Canary }, ["alice"]),
        ("forms", "inspections", "IN-7", new() { ["finding"] = "dent", ["fb_doc_id"] = "0f8e2c4a-1b2d-4c6e-9a0b-7d5e3f1a2b4c" }, ["alice"]),
        ("forms", "inspection_defects", "D-1", new() { ["part"] = "left door", ["fb_doc_id"] = "0f8e2c4a-1b2d-4c6e-9a0b-7d5e3f1a2b4c", ["fb_row"] = 1 }, ["alice"]),
    ];

    private static Task<LookupAnswer> Run(FakeSessions sessions, string start, string key, string[] path, string[] read) =>
        Walker.RunAsync(new LookupCall(new EntityKey(start, key), path, read), sessions, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_walk_across_two_systems_answers_with_the_record_it_read_and_the_relations_it_followed()
    {
        var answer = await Run(new FakeSessions("alice"), "WorkOrder", "WO-2", ["orders_item", "supplied_by"], ["name"]);

        var claim = Assert.Single(Assert.IsType<Answered>(answer).Claims);
        Assert.Equal(new EntityKey("Supplier", "S-2"), claim.Entity);
        Assert.Equal("name", claim.Field);
        Assert.Equal(Canary, claim.Value.GetString());
        Assert.Equal([new SourceRecord("erp", "visible_suppliers", "S-2")], claim.Sources);
        Assert.Equal(
            [
                new Hop("orders_item", new EntityKey("WorkOrder", "WO-2"), new EntityKey("Item", "I-200")),
                new Hop("supplied_by", new EntityKey("Item", "I-200"), new EntityKey("Supplier", "S-2")),
            ],
            claim.Path);
    }

    [Fact]
    public async Task The_same_question_answers_differently_for_someone_who_may_see_less()
    {
        var bobs = await Run(new FakeSessions("bob"), "WorkOrder", "WO-2", ["orders_item", "supplied_by"], ["name"]);

        var refusal = Assert.IsType<Refused>(bobs).Refusal;
        Assert.Equal(new Refusal(RefusalReason.NoVisibleResult, 2, "supplied_by", "erp"), refusal);
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(bobs, ContractJson.Default.LookupAnswer), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_start_entity_the_person_may_not_see_is_refused_at_hop_zero_without_reading_further()
    {
        var sessions = new FakeSessions("bob");

        var answer = await Run(sessions, "WorkOrder", "WO-3", ["orders_item", "supplied_by"], ["name"]);

        Assert.Equal(new Refusal(RefusalReason.NoVisibleResult, 0, null, "mes"), Assert.IsType<Refused>(answer).Refusal);
        Assert.Equal(["mes"], sessions.Opened);
    }

    [Fact]
    public async Task An_empty_field_that_names_the_next_entity_is_refused_where_the_walk_would_have_gone()
    {
        var answer = await Run(new FakeSessions("alice"), "WorkOrder", "WO-4", ["orders_item"], ["name"]);

        Assert.Equal(new Refusal(RefusalReason.NoVisibleResult, 1, "orders_item", "erp"), Assert.IsType<Refused>(answer).Refusal);
    }

    [Fact]
    public async Task An_empty_path_reads_the_start_entity_and_states_one_fact_per_field()
    {
        var answer = await Run(new FakeSessions("bob"), "Item", "I-100", [], ["name", "supplier"]);

        var claims = Assert.IsType<Answered>(answer).Claims;
        Assert.Equal(["Bracket", "S-1"], claims.Select(c => c.Value.GetString()));
        Assert.All(claims, c => Assert.Empty(c.Path));
    }

    [Fact]
    public async Task Each_system_is_opened_once_per_lookup_asked_only_for_the_columns_the_walk_needs_and_closed_after()
    {
        var sessions = new FakeSessions("alice");

        await Run(sessions, "WorkOrder", "WO-2", ["orders_item", "supplied_by"], ["name"]);

        Assert.Equal(["mes", "erp"], sessions.Opened);
        Assert.Equal(
            ["mes work_orders wo_no=WO-2 [item_code]", "erp visible_items item_code=I-200 [supplier_id]", "erp visible_suppliers supplier_id=S-2 [supplier_name]"],
            sessions.Reads);
        Assert.Equal(2, sessions.Disposed);
    }

    [Fact]
    public async Task A_type_that_declares_its_record_is_cited_by_the_record_read_not_by_its_key()
    {
        var sessions = new FakeSessions("alice");

        var inspection = Assert.Single(Assert.IsType<Answered>(await Run(sessions, "Inspection", "IN-7", [], ["finding"])).Claims);
        var defect = Assert.Single(Assert.IsType<Answered>(await Run(sessions, "Defect", "D-1", [], ["part"])).Claims);

        Assert.Equal(new EntityKey("Inspection", "IN-7"), inspection.Entity);
        Assert.Equal([new SourceRecord("forms", "inspections", "0f8e2c4a-1b2d-4c6e-9a0b-7d5e3f1a2b4c")], inspection.Sources);
        Assert.Equal([new SourceRecord("forms", "inspection_defects", "0f8e2c4a-1b2d-4c6e-9a0b-7d5e3f1a2b4c/1")], defect.Sources);
        Assert.Equal(
            ["forms inspections inspection_no=IN-7 [finding,fb_doc_id]", "forms inspection_defects defect_no=D-1 [part,fb_doc_id,fb_row]"],
            sessions.Reads);
    }

    [Fact]
    public async Task A_lookup_not_in_the_ontology_s_terms_reads_no_source()
    {
        var sessions = new FakeSessions("alice");

        await Assert.ThrowsAsync<UnknownTermException>(() => Run(sessions, "WorkOrder", "WO-1", ["supplied_by"], ["name"]));

        Assert.Empty(sessions.Opened);
    }

    private sealed class FixedCatalog(Ontology.Ontology ontology) : IOntologyCatalog
    {
        public ValueTask<Ontology.Ontology> GetAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(ontology);
    }

    private sealed class FakeSessions(string person) : ISourceSessions
    {
        public List<string> Opened { get; } = [];

        public List<string> Reads { get; } = [];

        public int Disposed { get; set; }

        public ValueTask<ISourceSession> OpenAsync(string system, CancellationToken cancellationToken = default)
        {
            Opened.Add(system);
            return ValueTask.FromResult<ISourceSession>(new Session(this, system));
        }

        private sealed class Session(FakeSessions owner, string system) : ISourceSession
        {
            public ValueTask<IReadOnlyList<SourceRow>> ReadAsync(string table, string keyColumn, string key, IReadOnlyList<string> columns, CancellationToken cancellationToken = default)
            {
                owner.Reads.Add($"{system} {table} {keyColumn}={key} [{string.Join(",", columns)}]");
                IReadOnlyList<SourceRow> rows =
                [
                    .. Rows
                        .Where(r => r.System == system && r.Table == table && r.Key == key && r.SeenBy.Contains(owner.Person))
                        .Select(r => new SourceRow(r.Key, columns.ToDictionary(c => c, c => JsonSerializer.SerializeToElement(r.Values[c])))),
                ];
                return ValueTask.FromResult(rows);
            }

            public ValueTask DisposeAsync()
            {
                owner.Disposed++;
                return ValueTask.CompletedTask;
            }
        }

        private string Person => person;
    }
}
