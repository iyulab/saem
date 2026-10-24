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
        new RelationDeclaration("orders_item", "WorkOrder", "item", "Item", Inverse: "ordered_by"),
        new RelationDeclaration("supplied_by", "Item", "supplier", "Supplier"),
    ];

    private static readonly DateTimeOffset ReadAt = new(2026, 10, 25, 2, 0, 0, TimeSpan.Zero);

    private static readonly LookupWalker Walker = new(new FixedCatalog(Ontology.Ontology.Create(Rig)), new FixedClock(ReadAt));

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

    private static Task<LookupAnswer> Find(FakeSessions sessions, string start, string field, string value, string[] path, string[] read, LookupWalker? walker = null) =>
        (walker ?? Walker).RunAsync(new LookupCall(new LookupStart(start, where: new FieldMatch(field, value)), path, read), sessions, TestContext.Current.CancellationToken);

    /// <summary>D-80 ④: a start named by a field's value that one visible entity holds is walked from that entity.</summary>
    [Fact]
    public async Task A_start_named_by_a_value_one_visible_entity_holds_walks_from_it()
    {
        var answer = await Find(new FakeSessions("alice"), "Supplier", "name", "Hanbit Parts", [], ["name"]);

        var claim = Assert.Single(Assert.IsType<Answered>(answer).Claims);
        Assert.Equal(new EntityKey("Supplier", "S-1"), claim.Entity);
    }

    /// <summary>Several visible entities hold the value: the walk does not start, and they are offered by key and record — only the person's own rows.</summary>
    [Fact]
    public async Task A_start_named_by_a_value_several_visible_entities_hold_answers_with_them_to_choose_from()
    {
        var alice = new FakeSessions("alice");
        var answer = await Find(alice, "WorkOrder", "item", "I-100", ["orders_item"], ["name"]);

        var candidates = Assert.IsType<Candidates>(answer).Entities;
        Assert.Equal([new EntityKey("WorkOrder", "WO-1"), new EntityKey("WorkOrder", "WO-3")], candidates.Select(c => c.Entity));
        Assert.Equal(new SourceRecord("mes", "work_orders", "WO-1") { ReadAt = ReadAt }, candidates[0].Source);
        Assert.Equal(["mes work_orders item_code=I-100 [] limit 51"], alice.Reads);
        // bob sees only WO-1 of the two: one match, so his lookup walks from it.
        Assert.IsType<Answered>(await Find(new FakeSessions("bob"), "WorkOrder", "item", "I-100", ["orders_item"], ["name"]));
    }

    [Fact]
    public async Task A_start_named_by_a_value_no_visible_entity_holds_is_refused_at_the_start()
    {
        var answer = await Find(new FakeSessions("bob"), "Supplier", "name", Canary, [], ["name"]);

        Assert.Equal(new Refusal(RefusalReason.NoVisibleResult, 0, null, "erp"), Assert.IsType<Refused>(answer).Refusal);
    }

    /// <summary>D-84 ①: more matches than one step may read is too broad — at the start, no count said.</summary>
    [Fact]
    public async Task A_start_value_more_entities_hold_than_the_limit_is_too_broad()
    {
        var narrow = new LookupWalker(new FixedCatalog(Ontology.Ontology.Create(Rig)), new FixedClock(ReadAt), maxCandidatesPerStep: 1);

        var thrown = await Assert.ThrowsAsync<TooBroadException>(() => Find(new FakeSessions("alice"), "WorkOrder", "item", "I-100", [], ["quantity"], narrow));

        Assert.Equal((0, (string?)null), (thrown.AtHop, thrown.Relation));
    }

    [Fact]
    public async Task A_start_named_by_an_undeclared_field_is_a_malformed_question_and_reads_nothing()
    {
        var sessions = new FakeSessions("alice");

        var thrown = await Assert.ThrowsAsync<UnknownTermException>(() => Find(sessions, "Supplier", "phone", "1", [], ["name"]));

        Assert.Contains("by 'phone', which is not a field of 'Supplier'", Assert.Single(thrown.Problems), StringComparison.Ordinal);
        Assert.Empty(sessions.Opened);
    }

    [Fact]
    public async Task A_walk_across_two_systems_answers_with_the_record_it_read_and_the_relations_it_followed()
    {
        var answer = await Run(new FakeSessions("alice"), "WorkOrder", "WO-2", ["orders_item", "supplied_by"], ["name"]);

        var claim = Assert.Single(Assert.IsType<Answered>(answer).Claims);
        Assert.Equal(new EntityKey("Supplier", "S-2"), claim.Entity);
        Assert.Equal("name", claim.Field);
        Assert.Equal(Canary, claim.Value.GetString());
        Assert.Equal([new SourceRecord("erp", "visible_suppliers", "S-2") { ReadAt = ReadAt }], claim.Sources);
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
        Assert.Equal([new SourceRecord("forms", "inspections", "0f8e2c4a-1b2d-4c6e-9a0b-7d5e3f1a2b4c") { ReadAt = ReadAt }], inspection.Sources);
        Assert.Equal([new SourceRecord("forms", "inspection_defects", "0f8e2c4a-1b2d-4c6e-9a0b-7d5e3f1a2b4c/1") { ReadAt = ReadAt }], defect.Sources);
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

    private static Task<LookupAnswer> Back(FakeSessions sessions, IMaterializedRelations relations, string start, string key, string[] path, string[] read, LookupWalker? walker = null) =>
        (walker ?? Walker).RunAsync(new LookupCall(new EntityKey(start, key), path, read), sessions, relations, TestContext.Current.CancellationToken);

    // g-plant's edges: the work orders that make I-100, as its pass found them.
    private static readonly Partitions Plant = new() { ["g-plant"] = [("orders_item", "I-100", "WO-1"), ("orders_item", "I-100", "WO-3")] };

    private static Hop Backwards(string item, string workOrder, string partition) =>
        new("ordered_by", new EntityKey("Item", item), new EntityKey("WorkOrder", workOrder)) { Partition = partition };

    /// <summary>Walked backwards, a relation answers every entity that leads to the one reached, read as the person, the hop naming its partition.</summary>
    [Fact]
    public async Task A_relation_walked_backwards_answers_each_entity_its_partition_leads_from()
    {
        var answer = await Back(new FakeSessions("alice"), Plant, "Item", "I-100", ["ordered_by"], ["quantity"]);

        var claims = Assert.IsType<Answered>(answer).Claims;
        Assert.Equal([("WO-1", 50), ("WO-3", 10)], claims.Select(c => (c.Entity.Key, c.Value.GetInt32())));
        Assert.Equal([new SourceRecord("mes", "work_orders", "WO-1") { ReadAt = ReadAt }], claims[0].Sources);
        Assert.Equal([Backwards("I-100", "WO-1", "g-plant")], claims[0].Path);
    }

    [Fact]
    public async Task A_hop_walked_backwards_says_when_its_partition_was_read()
    {
        var asOf = new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero);
        var plant = new Partitions { ["g-plant"] = Plant["g-plant"], ["g-office"] = [] };
        plant.AsOf["g-plant"] = asOf;

        var answer = await Back(new FakeSessions("alice"), plant, "Item", "I-100", ["ordered_by"], ["quantity"]);

        Assert.All(Assert.IsType<Answered>(answer).Claims, c => Assert.Equal(asOf, Assert.Single(c.Path).PartitionAsOf));
        Assert.Equal(["g-plant"], plant.AsOfAsked);
    }

    /// <summary>The edges are the group's; whether the person may read each entity is still the source's (R-5).</summary>
    [Fact]
    public async Task An_entity_the_person_may_not_read_is_not_answered_though_their_group_has_its_edge()
    {
        var answer = await Back(new FakeSessions("bob"), Plant, "Item", "I-100", ["ordered_by"], ["quantity"]);

        Assert.Equal(["WO-1"], Assert.IsType<Answered>(answer).Claims.Select(c => c.Entity.Key));
    }

    /// <summary>An edge built before the source changed adds nothing: the entity is kept only while its field still names the one reached.</summary>
    [Fact]
    public async Task An_edge_the_source_no_longer_agrees_with_is_not_answered()
    {
        var stale = new Partitions { ["g-plant"] = [("orders_item", "I-100", "WO-2"), ("orders_item", "I-100", "WO-1")] };

        var answer = await Back(new FakeSessions("alice"), stale, "Item", "I-100", ["ordered_by"], ["quantity"]);

        Assert.Equal(["WO-1"], Assert.IsType<Answered>(answer).Claims.Select(c => c.Entity.Key));
    }

    /// <summary>
    /// D-46 · D-80 ①: an edge whose entity the person's read does not show — out of their sight, or changed since —
    /// marks the partition blocked. One bit: no key, no count, and the facts stay only what the person read.
    /// </summary>
    [Fact]
    public async Task A_partition_that_led_to_an_entity_the_person_does_not_see_is_reported_blocked_and_no_more()
    {
        var answer = await Back(new FakeSessions("bob"), Plant, "Item", "I-100", ["ordered_by"], ["quantity"]);

        Assert.Equal(["WO-1"], Assert.IsType<Answered>(answer).Claims.Select(c => c.Entity.Key));
        Assert.Equal([new PartitionRead("g-plant", null, Blocked: true, Incomplete: false)], answer.Partitions);
        Assert.DoesNotContain("WO-3", string.Join(' ', answer.Partitions![0].Statements), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_partition_every_edge_of_which_the_person_reads_is_not_blocked()
    {
        var answer = await Back(new FakeSessions("alice"), Plant, "Item", "I-100", ["ordered_by"], ["quantity"]);

        Assert.Equal([new PartitionRead("g-plant", null, Blocked: false, Incomplete: false)], answer.Partitions);
        Assert.Empty(answer.Partitions![0].Statements);
    }

    /// <summary>Nothing visible and the group knew of something: refused — the source shows no row — and blocked, both true at once.</summary>
    [Fact]
    public async Task A_refusal_carries_the_blocked_partition_whose_edges_the_person_could_not_read()
    {
        var hidden = new Partitions { ["g-plant"] = [("orders_item", "I-100", "WO-3")] };

        var answer = await Back(new FakeSessions("bob"), hidden, "Item", "I-100", ["ordered_by"], ["quantity"]);

        Assert.Equal(RefusalReason.NoVisibleResult, Assert.IsType<Refused>(answer).Refusal.Reason);
        Assert.Equal([new PartitionRead("g-plant", null, Blocked: true, Incomplete: false)], answer.Partitions);
    }

    /// <summary>A partition built without some documents is read over what stands and says it is incomplete — a state, not a failure.</summary>
    [Fact]
    public async Task An_incomplete_partition_is_read_over_what_stands_and_says_so()
    {
        var partial = new Partitions { ["g-office"] = [], ["g-plant"] = Plant["g-plant"] };
        partial.Incomplete.Add("g-office");

        var answer = await Back(new FakeSessions("alice"), partial, "Item", "I-100", ["ordered_by"], ["quantity"]);

        Assert.Equal(["WO-1", "WO-3"], Assert.IsType<Answered>(answer).Claims.Select(c => c.Entity.Key));
        Assert.Equal(
            [new PartitionRead("g-office", null, Blocked: false, Incomplete: true), new PartitionRead("g-plant", null, Blocked: false, Incomplete: false)],
            answer.Partitions);
    }

    /// <summary>A partition that leads nowhere and holds all it was given says nothing, and is not listed.</summary>
    [Fact]
    public async Task A_partition_with_nothing_to_say_is_not_listed()
    {
        var answer = await Back(new FakeSessions("alice"), new Partitions { ["g-office"] = [], ["g-plant"] = Plant["g-plant"] }, "Item", "I-100", ["ordered_by"], ["quantity"]);

        Assert.Equal(["g-plant"], answer.Partitions!.Select(p => p.Name));
    }

    /// <summary>D-84 ①: a step with more candidates than the limit is not read at all — not one source read, and no count said.</summary>
    [Fact]
    public async Task A_step_with_more_candidates_than_the_limit_is_too_broad_and_reads_nothing()
    {
        var sessions = new FakeSessions("alice");
        var narrow = new LookupWalker(new FixedCatalog(Ontology.Ontology.Create(Rig)), new FixedClock(ReadAt), maxCandidatesPerStep: 1);

        var thrown = await Assert.ThrowsAsync<TooBroadException>(() => Back(sessions, Plant, "Item", "I-100", ["ordered_by"], ["quantity"], narrow));

        Assert.Equal((1, "ordered_by"), (thrown.AtHop, thrown.Relation));
        Assert.DoesNotContain("2", thrown.Message, StringComparison.Ordinal);
        Assert.Empty(sessions.Reads);
        Assert.Equal(["WO-1", "WO-3"], Assert.IsType<Answered>(await Back(new FakeSessions("alice"), Plant, "Item", "I-100", ["ordered_by"], ["quantity"],
            new LookupWalker(new FixedCatalog(Ontology.Ontology.Create(Rig)), new FixedClock(ReadAt), maxCandidatesPerStep: 2))).Claims.Select(c => c.Entity.Key));
    }

    /// <summary>D-80 ⑤: every answer names the version of the ontology it was walked in.</summary>
    [Fact]
    public async Task Every_answer_names_the_ontology_version_it_was_walked_in()
    {
        var version = Ontology.Ontology.Create(Rig).Version;

        Assert.Equal(version, (await Back(new FakeSessions("alice"), Plant, "Item", "I-100", ["ordered_by"], ["quantity"])).CatalogVersion);
        Assert.Equal(version, (await Back(new FakeSessions("alice"), Plant, "Item", "I-200", ["ordered_by"], ["quantity"])).CatalogVersion);
        Assert.Equal(version, (await Back(new FakeSessions("alice"), NoMaterializedRelations.Instance, "Item", "I-100", ["ordered_by"], ["quantity"])).CatalogVersion);
    }

    [Fact]
    public async Task Two_partitions_are_answered_each_on_its_own_never_merged()
    {
        var both = new Partitions { ["g-office"] = [("orders_item", "I-100", "WO-1")], ["g-plant"] = [("orders_item", "I-100", "WO-1")] };

        var answer = await Back(new FakeSessions("alice"), both, "Item", "I-100", ["ordered_by"], ["quantity"]);

        Assert.Equal(["g-office", "g-plant"], Assert.IsType<Answered>(answer).Claims.Select(c => c.Path[^1].Partition));
    }

    /// <summary>T-41: a person in no group with a partition is refused with that reason, and no source is read.</summary>
    [Fact]
    public async Task A_person_in_no_partition_is_refused_for_that_reason_before_any_source_is_read()
    {
        var sessions = new FakeSessions("alice");

        var answer = await Back(sessions, NoMaterializedRelations.Instance, "Item", "I-100", ["ordered_by"], ["quantity"]);

        Assert.Equal(new Refusal(RefusalReason.NoMaterializedPartition, 1, "ordered_by", "mes"), Assert.IsType<Refused>(answer).Refusal);
        Assert.Empty(sessions.Opened);
    }

    [Fact]
    public async Task A_partition_without_an_edge_to_the_entity_is_refused_at_the_backward_step()
    {
        var answer = await Back(new FakeSessions("alice"), Plant, "Item", "I-200", ["ordered_by"], ["quantity"]);

        Assert.Equal(new Refusal(RefusalReason.NoVisibleResult, 1, "ordered_by", "mes"), Assert.IsType<Refused>(answer).Refusal);
    }

    [Fact]
    public async Task A_backward_step_follows_the_forward_ones_and_the_path_carries_them_all()
    {
        var answer = await Back(new FakeSessions("alice"), Plant, "WorkOrder", "WO-3", ["orders_item", "ordered_by"], ["quantity"]);

        var claims = Assert.IsType<Answered>(answer).Claims;
        Assert.Equal(["WO-1", "WO-3"], claims.Select(c => c.Entity.Key));
        Assert.Equal(
            [new Hop("orders_item", new EntityKey("WorkOrder", "WO-3"), new EntityKey("Item", "I-100")), Backwards("I-100", "WO-1", "g-plant")],
            claims[0].Path);
    }

    /// <summary>Each group's edges as its pass found them: (relation, entity led to, entity it leaves).</summary>
    private sealed class Partitions : Dictionary<string, (string Relation, string To, string From)[]>, IMaterializedRelations
    {
        IReadOnlyList<string> IMaterializedRelations.Partitions => [.. Keys.Order(StringComparer.Ordinal)];

        public Dictionary<string, DateTimeOffset> AsOf { get; } = [];

        public List<string> AsOfAsked { get; } = [];

        public HashSet<string> Incomplete { get; } = [];

        public ValueTask<PartitionEdges> LeadingToAsync(string partition, RelationDeclaration relation, string toKey, int limit, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new PartitionEdges(
                [.. this[partition].Where(e => e.Relation == relation.Name && e.To == toKey).Select(e => e.From).Take(limit + 1)],
                Incomplete.Contains(partition)));

        public ValueTask<DateTimeOffset?> AsOfAsync(string partition, CancellationToken cancellationToken = default)
        {
            AsOfAsked.Add(partition);
            return ValueTask.FromResult<DateTimeOffset?>(AsOf.TryGetValue(partition, out var asOf) ? asOf : null);
        }
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

            public ValueTask<IReadOnlyList<SourceRow>> FindAsync(string table, string keyColumn, string column, string value, IReadOnlyList<string> columns, int limit, CancellationToken cancellationToken = default)
            {
                owner.Reads.Add($"{system} {table} {column}={value} [{string.Join(",", columns)}] limit {limit}");
                IReadOnlyList<SourceRow> rows =
                [
                    .. Rows
                        .Where(r => r.System == system && r.Table == table && r.SeenBy.Contains(owner.Person)
                            && r.Values.TryGetValue(column, out var held) && Convert.ToString(held, System.Globalization.CultureInfo.InvariantCulture) == value)
                        .OrderBy(r => r.Key, StringComparer.Ordinal)
                        .Take(limit)
                        .Select(r => new SourceRow(r.Key, columns.ToDictionary(c => c, c => JsonSerializer.SerializeToElement(r.Values[c])))),
                ];
                return ValueTask.FromResult(rows);
            }

            public IAsyncEnumerable<SourceRow> ScanAsync(string table, string keyColumn, IReadOnlyList<string> columns, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("A walk reads rows by key or finds them by a field; it never scans a table.");

            public ValueTask DisposeAsync()
            {
                owner.Disposed++;
                return ValueTask.CompletedTask;
            }
        }

        private string Person => person;
    }

    [Fact]
    public async Task A_source_says_when_it_was_read_and_says_so_on_the_wire()
    {
        var answer = Assert.IsType<Answered>(await Run(new FakeSessions("alice"), "WorkOrder", "WO-1", [], ["quantity"]));

        var source = Assert.Single(Assert.Single(answer.Claims).Sources);
        Assert.Equal(ReadAt, source.ReadAt);
        var wire = System.Text.Json.JsonSerializer.SerializeToElement<LookupAnswer>(answer, ContractJson.Default.LookupAnswer);
        Assert.Equal("2026-10-25T02:00:00+00:00", wire.GetProperty("claims")[0].GetProperty("sources")[0].GetProperty("readAt").GetString());
        var named = System.Text.Json.JsonSerializer.SerializeToElement(new SourceRecord("mes", "work_orders", "WO-1"), ContractJson.Default.SourceRecord);
        Assert.False(named.TryGetProperty("readAt", out _), "a record named without being read has no read time");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
