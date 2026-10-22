using Eyu.Core.Grounding;
using Eyu.Core.Primitives;
using Eyu.Core.Proposals;
using Eyu.Core.Records;
using Saem.Ontology;
using Xunit;

namespace Saem.Authoring.Tests;

/// <summary>
/// From entities to types: a proposal about records becomes declarations of the tables those records
/// are rows of, the existing declarations stay the authority, and whatever cannot be declared is a
/// note naming its records rather than a guess.
/// </summary>
public sealed class DeclarationDrafterTests
{
    // Records of three subjects on two systems — the rig's shape, written as rows.
    private static readonly RawRecord Wo1 = Row("wo-1", "work_orders", ("wo_no", "WO-1"), ("item_code", "I-1"), ("qty", "5"), ("note", "rush"));
    private static readonly RawRecord Wo2 = Row("wo-2", "work_orders", ("wo_no", "WO-2"), ("item_code", "I-2"), ("qty", "1"), ("note", null));
    private static readonly RawRecord Item1 = Row("item-1", "items", ("item_code", "I-1"), ("item_name", "Bolt"), ("supplier_id", "S-1"));
    private static readonly RawRecord Item2 = Row("item-2", "items", ("item_code", "I-2"), ("item_name", "Nut"), ("supplier_id", "S-1"));
    private static readonly RawRecord Sup1 = Row("sup-1", "suppliers", ("supplier_id", "S-1"), ("supplier_name", "Acme"));

    private static readonly IReadOnlyList<RawRecord> Records = [Wo1, Wo2, Item1, Item2, Sup1];

    private static readonly Dictionary<string, SubjectSource> Sources = new()
    {
        ["work_orders"] = new("mes", "work_orders", "wo_no"),
        ["items"] = new("erp", "visible_items", "item_code"),
        ["suppliers"] = new("erp", "visible_suppliers", "supplier_id"),
    };

    // The rig's hand-written declarations: friendlier field names over the same columns.
    private static readonly IReadOnlyList<Declaration> Rig =
    [
        new EntityTypeDeclaration("WorkOrder", "mes", "work_orders", "wo_no",
            new Dictionary<string, string> { ["note"] = "note", ["quantity"] = "qty", ["item"] = "item_code" }),
        new EntityTypeDeclaration("Item", "erp", "visible_items", "item_code",
            new Dictionary<string, string> { ["name"] = "item_name", ["supplier"] = "supplier_id" }),
        new EntityTypeDeclaration("Supplier", "erp", "visible_suppliers", "supplier_id",
            new Dictionary<string, string> { ["name"] = "supplier_name" }),
        new RelationDeclaration("orders_item", "WorkOrder", "item", "Item"),
        new RelationDeclaration("supplied_by", "Item", "supplier", "Supplier"),
    ];

    private static OntologyProposal RigProposal() => Proposal(
        [
            Entity("e1", "WorkOrder", ["wo-1"]),
            Entity("e2", "WorkOrder", ["wo-2"]),
            Entity("e3", "Item", ["item-1"], mentionedIn: ["wo-1"]),
            Entity("e4", "Item", ["item-2"], mentionedIn: ["wo-2"]),
            Entity("e5", "Supplier", ["sup-1"], mentionedIn: ["item-1", "item-2"]),
        ],
        [
            Relation("orders_item", "e1", "e3", "wo-1", "item-1"),
            Relation("orders_item", "e2", "e4", "wo-2", "item-2"),
            Relation("supplied_by", "e3", "e5", "item-1", "sup-1"),
            Relation("supplied_by", "e4", "e5", "item-2", "sup-1"),
        ]);

    [Fact]
    public void A_proposal_over_the_rig_records_drafts_the_rig_ontology()
    {
        var draft = DeclarationDrafter.Draft(RigProposal(), Records, Sources);

        Assert.Empty(draft.Notes);
        var ontology = Ontology.Ontology.Create(draft.Declarations);

        var workOrder = ontology.EntityTypes["WorkOrder"];
        Assert.Equal(("mes", "work_orders", "wo_no"), (workOrder.System, workOrder.Table, workOrder.Key));
        Assert.Equal(["item_code", "note", "qty"], workOrder.Fields.Keys);

        var item = ontology.EntityTypes["Item"];
        Assert.Equal(("erp", "visible_items", "item_code"), (item.System, item.Table, item.Key));
        Assert.Equal(["item_name", "supplier_id"], item.Fields.Keys);

        Assert.Equal(new RelationDeclaration("orders_item", "WorkOrder", "item_code", "Item"), ontology.Relations["orders_item"]);
        Assert.Equal(new RelationDeclaration("supplied_by", "Item", "supplier_id", "Supplier"), ontology.Relations["supplied_by"]);
    }

    [Fact]
    public void Added_declarations_come_types_first_then_relations_each_by_name_with_their_records()
    {
        var draft = DeclarationDrafter.Draft(RigProposal(), Records, Sources);

        Assert.Equal(["Item", "Supplier", "WorkOrder", "orders_item", "supplied_by"], draft.Added.Select(d => d.Name));
        Assert.Equal(["item-1", "item-2"], draft.Evidence["Item"]);
        Assert.Equal(["wo-1", "item-1", "wo-2", "item-2"], draft.Evidence["orders_item"]);
    }

    [Fact]
    public void What_is_already_declared_is_kept_and_a_relation_is_read_through_the_declared_field_name()
    {
        var draft = DeclarationDrafter.Draft(RigProposal(), Records, Sources, Rig);

        Assert.Empty(draft.Added);
        Assert.Empty(draft.Notes);
        Assert.Equal(Rig, draft.Declarations);
    }

    [Fact]
    public void A_relation_new_to_the_declarations_leaves_its_type_through_the_declared_field()
    {
        var existing = Rig.Where(d => d.Name != "orders_item").ToList();

        var draft = DeclarationDrafter.Draft(RigProposal(), Records, Sources, existing);

        var added = Assert.Single(draft.Added);
        Assert.Equal(new RelationDeclaration("orders_item", "WorkOrder", "item", "Item"), added);
        Assert.Equal(existing, draft.Declarations.Take(existing.Count));
    }

    [Fact]
    public void A_proposed_type_on_a_declared_types_table_is_read_as_that_type_whatever_it_is_called()
    {
        // Eyu names an entity after its subject when the subject is declared — the form type, not the
        // ontology's name for the kind. The table says which declared type it is.
        var proposal = RigProposal();
        var renamed = proposal with
        {
            Entities = [.. proposal.Entities.Select(e => EntityProposal.Create(
                e.EntityId, e.Name, e.EntityType switch { "WorkOrder" => "work_orders", "Item" => "items", _ => e.EntityType },
                e.Claim, e.Origin, e.Confidence, denotedBy: e.DenotedBy))],
        };
        var existing = Rig.OfType<EntityTypeDeclaration>().Cast<Declaration>().ToList();

        var draft = DeclarationDrafter.Draft(renamed, Records, Sources, existing);

        Assert.Empty(draft.Notes);
        Assert.Equal(
            [new RelationDeclaration("orders_item", "WorkOrder", "item", "Item"), new RelationDeclaration("supplied_by", "Item", "supplier", "Supplier")],
            draft.Added);
    }

    [Fact]
    public void Two_proposed_types_on_one_table_are_drafted_as_one_and_the_second_is_noted()
    {
        var proposal = RigProposal();
        var split = proposal with
        {
            Entities = [.. proposal.Entities.Select(e => e.EntityId == "e4"
                ? EntityProposal.Create(e.EntityId, e.Name, "Part", e.Claim, e.Origin, e.Confidence, denotedBy: e.DenotedBy)
                : e)],
        };

        var draft = DeclarationDrafter.Draft(split, Records, Sources);

        Assert.DoesNotContain(draft.Added, d => d.Name == "Part");
        var note = Assert.Single(draft.Notes);
        Assert.Equal("Part", note.About);
        Assert.Contains("read as 'Item'", note.Reason, StringComparison.Ordinal);
        Assert.Contains(new RelationDeclaration("orders_item", "WorkOrder", "item_code", "Item"), draft.Added);
    }

    [Fact]
    public void A_type_declared_on_another_table_is_kept_as_declared_and_the_difference_is_noted()
    {
        var existing = Rig.Select(d => d is EntityTypeDeclaration { Name: "Item" } t ? t with { Table = "items_v2" } : d).ToList();

        var draft = DeclarationDrafter.Draft(RigProposal(), Records, Sources, existing);

        var note = Assert.Single(draft.Notes);
        Assert.Equal("Item", note.About);
        Assert.Contains("erp.items_v2", note.Reason, StringComparison.Ordinal);
        Assert.Contains("erp.visible_items", note.Reason, StringComparison.Ordinal);
        Assert.Equal(existing, draft.Declarations);
    }

    [Fact]
    public void Columns_the_records_carry_that_no_declared_field_holds_are_noted()
    {
        var existing = Rig.Select(d => d is EntityTypeDeclaration { Name: "WorkOrder" } t
            ? t with { Fields = new Dictionary<string, string> { ["item"] = "item_code" } }
            : d).ToList();

        var draft = DeclarationDrafter.Draft(RigProposal(), Records, Sources, existing);

        var note = Assert.Single(draft.Notes);
        Assert.Equal("WorkOrder", note.About);
        Assert.Contains("note, qty", note.Reason, StringComparison.Ordinal);
        Assert.Empty(draft.Added);
    }

    [Fact]
    public void A_type_whose_entities_are_only_mentioned_is_not_declared_and_neither_is_a_relation_to_it()
    {
        var proposal = Proposal(
            [Entity("e1", "WorkOrder", ["wo-1"]), Entity("e2", "Machine", [], mentionedIn: ["wo-1"])],
            [Relation("ran_on", "e1", "e2", "wo-1")]);

        var draft = DeclarationDrafter.Draft(proposal, Records, Sources);

        Assert.Equal(["WorkOrder"], draft.Added.Select(d => d.Name));
        Assert.Collection(draft.Notes,
            machine =>
            {
                Assert.Equal("Machine", machine.About);
                Assert.Equal(["wo-1"], machine.Records);
            },
            ranOn =>
            {
                Assert.Equal("ran_on", ranOn.About);
                Assert.Contains("'Machine' is not declared", ranOn.Reason, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void A_relation_also_stated_two_hops_away_is_declared_between_the_pair_a_field_carries()
    {
        // Seen from a model: «supplied_by» from the item to its supplier, and again from the work order
        // to the supplier of its item — the second carried by no field of the work order.
        var proposal = RigProposal();
        var transitive = proposal with
        {
            Relations = [.. proposal.Relations, Relation("supplied_by", "e1", "e5", "wo-1", "item-1", "sup-1"), Relation("supplied_by", "e2", "e5", "wo-2", "item-2", "sup-1")],
        };

        var draft = DeclarationDrafter.Draft(transitive, Records, Sources);

        Assert.Contains(new RelationDeclaration("supplied_by", "Item", "supplier_id", "Supplier"), draft.Added);
        var note = Assert.Single(draft.Notes);
        Assert.Equal("supplied_by", note.About);
        Assert.StartsWith("WorkOrder → Supplier: No field", note.Reason, StringComparison.Ordinal);
        Assert.Equal(["item-1", "sup-1", "item-2"], draft.Evidence["supplied_by"]);
    }

    [Fact]
    public void A_name_two_pairs_of_types_each_carry_by_a_field_is_not_declared_as_either()
    {
        var proposal = RigProposal();
        var reused = proposal with
        {
            Relations = [.. proposal.Relations.Select(r => r.RelationName == "orders_item"
                ? RelationProposal.Create("supplied_by", r.FromEntityId, r.ToEntityId, r.Claim, r.Origin, r.Confidence)
                : r)],
        };

        var draft = DeclarationDrafter.Draft(reused, Records, Sources);

        Assert.DoesNotContain(draft.Added, d => d is RelationDeclaration);
        Assert.Contains("own name", Assert.Single(draft.Notes).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_denoted_by_records_of_two_subjects_has_no_one_table()
    {
        var proposal = Proposal([Entity("e1", "Thing", ["wo-1", "item-1"])], []);

        var draft = DeclarationDrafter.Draft(proposal, Records, Sources);

        Assert.Empty(draft.Added);
        var note = Assert.Single(draft.Notes);
        Assert.Contains("items, work_orders", note.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_subject_no_table_was_given_for_is_named()
    {
        var sources = new Dictionary<string, SubjectSource>(Sources);
        sources.Remove("suppliers");

        var draft = DeclarationDrafter.Draft(RigProposal(), Records, sources);

        Assert.DoesNotContain(draft.Added, d => d.Name == "Supplier");
        Assert.Contains(draft.Notes, n => n.About == "Supplier" && n.Reason.Contains("'suppliers'", StringComparison.Ordinal));
        Assert.Contains(draft.Notes, n => n.About == "supplied_by");
    }

    [Fact]
    public void A_record_without_its_key_cannot_place_the_type()
    {
        var keyless = Row("sup-2", "suppliers", ("supplier_name", "Orphan"));
        var proposal = Proposal([Entity("e1", "Supplier", ["sup-1", "sup-2"])], []);

        var draft = DeclarationDrafter.Draft(proposal, [.. Records, keyless], Sources);

        Assert.Empty(draft.Added);
        Assert.Contains("sup-2", Assert.Single(draft.Notes).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_fields_holding_the_target_key_leave_the_relation_undeclared_unless_the_claim_cites_one()
    {
        // The order also records the item it replaced — the same code, so either field could be the link.
        var wo = Row("wo-1", "work_orders", ("wo_no", "WO-1"), ("item_code", "I-1"), ("replaces", "I-1"));
        RawRecord[] records = [wo, Item1];
        IReadOnlyList<EntityProposal> entities = [Entity("e1", "WorkOrder", ["wo-1"]), Entity("e2", "Item", ["item-1"])];

        var ambiguous = DeclarationDrafter.Draft(Proposal(entities, [Relation("orders_item", "e1", "e2", "wo-1", "item-1")]), records, Sources);
        Assert.DoesNotContain(ambiguous.Added, d => d is RelationDeclaration);
        Assert.Contains("item_code, replaces", Assert.Single(ambiguous.Notes).Reason, StringComparison.Ordinal);

        var claim = GroundedClaim.Create("orders_item", [new SourceRef("wo-1", "item_code"), new SourceRef("item-1")]);
        var cited = RelationProposal.Create("orders_item", "e1", "e2", claim, VocabularyOrigin.Acquired, 0.9);
        var resolved = DeclarationDrafter.Draft(Proposal(entities, [cited]), records, Sources);
        Assert.Empty(resolved.Notes);
        Assert.Contains(new RelationDeclaration("orders_item", "WorkOrder", "item_code", "Item"), resolved.Added);
    }

    [Fact]
    public void Instances_that_disagree_on_the_field_leave_the_relation_undeclared()
    {
        var wo2 = Row("wo-2", "work_orders", ("wo_no", "WO-2"), ("part", "I-2"));
        var proposal = Proposal(
            [Entity("e1", "WorkOrder", ["wo-1"]), Entity("e2", "WorkOrder", ["wo-2"]), Entity("e3", "Item", ["item-1"]), Entity("e4", "Item", ["item-2"])],
            [Relation("orders_item", "e1", "e3", "wo-1", "item-1"), Relation("orders_item", "e2", "e4", "wo-2", "item-2")]);

        var draft = DeclarationDrafter.Draft(proposal, [Wo1, wo2, Item1, Item2], Sources);

        Assert.DoesNotContain(draft.Added, d => d is RelationDeclaration);
        Assert.Contains("in every instance", Assert.Single(draft.Notes, n => n.About == "orders_item").Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_relation_already_declared_under_another_name_is_not_added_again()
    {
        var proposal = RigProposal();
        var renamed = proposal with
        {
            Relations = proposal.Relations.Select(r => r.RelationName == "orders_item"
                ? RelationProposal.Create("for_item", r.FromEntityId, r.ToEntityId, r.Claim, r.Origin, r.Confidence)
                : r).ToList(),
        };

        var draft = DeclarationDrafter.Draft(renamed, Records, Sources, Rig);

        Assert.Empty(draft.Added);
        Assert.Contains("'orders_item'", Assert.Single(draft.Notes).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Columns_named_otherwise_than_the_fields_are_declared_by_column()
    {
        var sources = new Dictionary<string, SubjectSource>(Sources)
        {
            ["items"] = new("erp", "visible_items", "item_code", new Dictionary<string, string> { ["item_name"] = "name_ko" }),
        };

        var draft = DeclarationDrafter.Draft(RigProposal(), Records, sources);

        var item = Assert.Single(draft.Added.OfType<EntityTypeDeclaration>(), t => t.Name == "Item");
        Assert.Equal("name_ko", item.Fields["item_name"]);
    }

    [Fact]
    public async Task The_draft_written_out_is_read_back_by_the_file_catalog_as_the_same_declarations()
    {
        var path = Path.Combine(Path.GetTempPath(), $"saem-draft-{Guid.NewGuid():N}.json");
        try
        {
            var existing = Rig.Where(d => d.Name != "supplied_by").ToList();
            var draft = DeclarationDrafter.Draft(RigProposal(), Records, Sources, existing);
            await File.WriteAllTextAsync(path, draft.ToJson(), TestContext.Current.CancellationToken);

            var read = await new FileOntologyCatalog(path).GetAsync(TestContext.Current.CancellationToken);

            Assert.Equal(["WorkOrder", "Item", "Supplier"], read.EntityTypes.Keys);
            Assert.Equal(new RelationDeclaration("supplied_by", "Item", "supplier", "Supplier"), read.Relations["supplied_by"]);
            Assert.Equal(Rig.OfType<EntityTypeDeclaration>().Select(t => t.Fields), read.EntityTypes.Values.Select(t => t.Fields));
            // Each declaration reads as it is written by hand: its kind, then its name.
            Assert.Contains("\"kind\": \"entityType\",\n    \"name\": \"WorkOrder\"", draft.ToJson(), StringComparison.Ordinal);
            Assert.Contains("\"kind\": \"relation\",\n    \"name\": \"supplied_by\"", draft.ToJson(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Inconsistent_existing_declarations_are_refused_before_anything_is_drafted()
    {
        IReadOnlyList<Declaration> broken = [new RelationDeclaration("orders_item", "WorkOrder", "item", "Item")];

        Assert.Throws<OntologyDeclarationException>(() => DeclarationDrafter.Draft(RigProposal(), Records, Sources, broken));
    }

    private static RawRecord Row(string id, string subject, params (string Field, string? Value)[] fields) =>
        new(id, fields.ToDictionary(f => f.Field, f => f.Value), SubjectRef.Create(subject));

    private static EntityProposal Entity(string id, string type, IReadOnlyList<string> denotedBy, IReadOnlyList<string>? mentionedIn = null)
    {
        var cited = denotedBy.Concat(mentionedIn ?? []).Select(r => new SourceRef(r)).ToList();
        return EntityProposal.Create(id, $"{type} {id}", type, GroundedClaim.Create(type, cited), VocabularyOrigin.Acquired, 0.9, denotedBy: denotedBy);
    }

    private static RelationProposal Relation(string name, string from, string to, params string[] records) =>
        RelationProposal.Create(name, from, to, GroundedClaim.Create(name, records.Select(r => new SourceRef(r)).ToList()), VocabularyOrigin.Acquired, 0.9);

    private static OntologyProposal Proposal(IReadOnlyList<EntityProposal> entities, IReadOnlyList<RelationProposal> relations) =>
        new(entities, relations, []);
}
