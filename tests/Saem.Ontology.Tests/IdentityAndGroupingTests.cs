using Saem.Contracts;
using Xunit;

namespace Saem.Ontology.Tests;

/// <summary>
/// D-82 ③ ④: an identity says when entities of two types are one — by fields that must hold the same value, or by
/// a linkage rule — and who settles it; a grouping is a node that is nothing but its members, so its shape has no
/// field of its own. Both stand only on declared types and fields, and are refused with every problem named.
/// </summary>
public sealed class IdentityAndGroupingTests
{
    // The MES keeps its own material master; each material names the ERP item it is by the item's code.
    private static readonly EntityTypeDeclaration Material = new(
        "Material", "mes", "materials", "mat_no",
        new Dictionary<string, string> { ["erpCode"] = "erp_item_code", ["note"] = "remark", ["since"] = "created_at" })
    {
        Kinds = new Dictionary<string, FieldKind> { ["note"] = FieldKind.FreeText, ["since"] = FieldKind.Date },
    };

    private static readonly EntityTypeDeclaration WorkOrder = OntologyTests.WorkOrder with
    {
        Fields = new Dictionary<string, string> { ["item"] = "item_code", ["quantity"] = "qty", ["line"] = "line_code" },
    };

    private static readonly EntityTypeDeclaration Item = OntologyTests.Item with
    {
        Fields = new Dictionary<string, string> { ["name"] = "item_name", ["supplier"] = "supplier_id", ["code"] = "item_code" },
    };

    private static readonly IdentityDeclaration SameItem = new("same_item", "Material", "Item", IdentityDecision.Automatic)
    {
        Match = [new FieldPair("erpCode", "code")],
    };

    private static readonly GroupingDeclaration Line = new("Line", [new GroupMembership("WorkOrder", "line")]);

    private static IdentityDeclaration Named(string name) => new(name, SameItem.First, SameItem.Second, SameItem.Decision) { Match = SameItem.Match };

    private static Ontology Create(params Declaration[] more) => Ontology.Create([WorkOrder, Item, Material, OntologyTests.Supplier, .. more]);

    private static IReadOnlyList<string> Refused(params Declaration[] more) =>
        Assert.Throws<OntologyDeclarationException>(() => Create(more)).Problems;

    [Fact]
    public void Identities_and_groupings_on_declared_types_and_fields_are_part_of_the_ontology_and_its_version()
    {
        var ontology = Create(SameItem, Line);

        Assert.Equal(SameItem, ontology.Identities["same_item"]);
        Assert.Equal(Line, ontology.Groupings["Line"]);
        Assert.NotEqual(Create().Version, ontology.Version);
        Assert.NotEqual(ontology.Version, Create(SameItem with { Decision = IdentityDecision.Confirmed }, Line).Version);
        Assert.Equal(ontology.Version, Ontology.Create([Line, SameItem, OntologyTests.Supplier, Material, Item, WorkOrder]).Version);
    }

    // D-87 ⓐ: the rule is a declaration of its own — the fields compared and how — which an identity names.
    private static readonly LinkageDeclaration SupplierNames = new(
        "supplier-names", [new LinkageComparison("name", "name", LinkageMethod.JaroWinkler, [0.95, 0.9])])
    {
        MatchThreshold = 6,
        NonMatchThreshold = -2,
    };

    private static readonly IdentityDeclaration SameSupplier = new("same_supplier", "Supplier", "Supplier", IdentityDecision.Confirmed) { Linkage = "supplier-names" };

    [Fact]
    public void An_identity_by_a_linkage_rule_and_one_within_a_single_type_are_both_declarable()
    {
        var ontology = Create(SupplierNames, SameSupplier);

        Assert.Equal("supplier-names", ontology.Identities["same_supplier"].Linkage);
        Assert.Equal(SupplierNames, ontology.Linkages["supplier-names"]);
        Assert.NotEqual(ontology.Version, Create(SupplierNames with { MatchThreshold = 7 }, SameSupplier).Version);
    }

    [Fact]
    public void A_linkage_rule_travels_as_its_own_kind_and_an_omitted_method_is_equality()
    {
        const string json = """
            [{ "kind": "linkage", "name": "codes", "compare": [{ "first": "erpCode", "second": "code" }, { "first": "note", "second": "name", "method": "tokenJaccard", "thresholds": [0.6] }] }]
            """;

        var rule = Assert.IsType<LinkageDeclaration>(Assert.Single(System.Text.Json.JsonSerializer.Deserialize(json, OntologyJson.Default.IReadOnlyListDeclaration)!));

        Assert.Equal(LinkageMethod.Exact, rule.Compare[0].Method);
        Assert.Null(rule.Compare[0].Thresholds);
        Assert.Equal([0.6], rule.Compare[1].Thresholds!);
        Assert.Null(rule.MatchThreshold);
        Assert.Contains("\"method\":\"tokenJaccard\"", System.Text.Json.JsonSerializer.Serialize<IReadOnlyList<Declaration>>([rule], OntologyJson.Default.IReadOnlyListDeclaration), StringComparison.Ordinal);
    }

    [Fact]
    public void A_linkage_rule_that_cannot_score_is_refused_with_each_reason()
    {
        static LinkageDeclaration Rule(LinkageComparison compared) => new("r", [compared]);

        Assert.Equal(
            [
                "Linkage rule 'r' compares no field.",
                "Linkage rule 'r' compares an empty field.",
                "Linkage rule 'r' compares 'a' with 'b' exactly, which takes no similarity threshold.",
                "Linkage rule 'r' compares 'a' with 'b' by shared words with no threshold — how many words must be shared has no default.",
                "Linkage rule 'r' compares 'a' with 'b' with a threshold outside 0 to 1.",
                "Linkage rule 'r' compares 'a' with 'b' with thresholds that are not highest first.",
                "Linkage rule 'r' compares 'a' with 'b' more than once.",
                "Linkage rule 'r' puts its match threshold (1) at or below its non-match threshold (1).",
                "Linkage rule 'r' has a threshold that is not a number.",
                "Linkage rule 'r' is declared more than once.",
            ],
            [
                .. Refused(new LinkageDeclaration("r", [])),
                .. Refused(Rule(new LinkageComparison("a", " "))),
                .. Refused(Rule(new LinkageComparison("a", "b", LinkageMethod.Exact, [1.0]))),
                .. Refused(Rule(new LinkageComparison("a", "b", LinkageMethod.TokenJaccard))),
                .. Refused(Rule(new LinkageComparison("a", "b", LinkageMethod.JaroWinkler, [1.2]))),
                .. Refused(Rule(new LinkageComparison("a", "b", LinkageMethod.MongeElkan, [0.9, 0.9]))),
                .. Refused(new LinkageDeclaration("r", [new LinkageComparison("a", "b"), new LinkageComparison("a", "b", LinkageMethod.JaroWinkler)])),
                .. Refused(Rule(new LinkageComparison("a", "b")) with { MatchThreshold = 1, NonMatchThreshold = 1 }),
                .. Refused(Rule(new LinkageComparison("a", "b")) with { MatchThreshold = double.NaN }),
                .. Refused(Rule(new LinkageComparison("a", "b")), Rule(new LinkageComparison("a", "b"))),
            ]);
    }

    [Fact]
    public void An_identity_names_a_declared_rule_whose_fields_its_two_types_have()
    {
        var byRule = new IdentityDeclaration("i", "Material", "Item", IdentityDecision.Confirmed) { Linkage = "names" };
        var rule = new LinkageDeclaration("names", [new LinkageComparison("erpCode", "code"), new LinkageComparison("erp", "sku", LinkageMethod.JaroWinkler)]);

        Assert.Equal(
            [
                "Identity 'i' names the linkage rule 'names', which is not declared.",
                "Identity 'i' scores by 'names', which compares 'erp', not a field of 'Material'.",
                "Identity 'i' scores by 'names', which compares 'sku', not a field of 'Item'.",
            ],
            [.. Refused(byRule), .. Refused(rule, byRule)]);
    }

    [Fact]
    public void Within_one_type_a_rule_compares_each_field_with_itself()
    {
        var within = new IdentityDeclaration("dup", "Item", "Item", IdentityDecision.Confirmed) { Linkage = "cross" };

        Assert.Equal(
            ["Identity 'dup' is within 'Item' and scores by 'cross', which compares 'name' with 'code'; two rows of one type are compared field by field."],
            Refused(new LinkageDeclaration("cross", [new LinkageComparison("name", "code", LinkageMethod.JaroWinkler)]), within));
        Create(new LinkageDeclaration("cross", [new LinkageComparison("name", "name", LinkageMethod.JaroWinkler)]), within);
    }

    [Fact]
    public void Free_text_is_never_required_equal_but_may_be_scored_as_alike()
    {
        var byRule = new IdentityDeclaration("i", "Material", "Item", IdentityDecision.Confirmed) { Linkage = "notes" };

        Assert.Contains(
            "Identity 'i' scores by 'notes', which requires equal 'Material.note', which holds free text",
            Assert.Single(Refused(new LinkageDeclaration("notes", [new LinkageComparison("note", "name")]), byRule)),
            StringComparison.Ordinal);

        Create(new LinkageDeclaration("notes", [new LinkageComparison("note", "name", LinkageMethod.TokenJaccard, [0.5])]), byRule);
    }

    [Fact]
    public void An_identity_tells_one_way_either_by_fields_or_by_a_rule()
    {
        var neither = new IdentityDeclaration("i", "Material", "Item", IdentityDecision.Automatic);

        Assert.Equal(
            [
                "Identity 'i' must declare either a field match or a linkage rule, and not both.",
                "Identity 'i' must declare either a field match or a linkage rule, and not both.",
                "Identity 'i' declares a field match of no fields.",
                "Identity 'i' names an empty linkage rule.",
            ],
            [
                .. Refused(neither),
                .. Refused(neither with { Match = SameItem.Match, Linkage = "rule" }),
                .. Refused(neither with { Match = [] }),
                .. Refused(neither with { Linkage = " " }),
            ]);
    }

    [Fact]
    public void An_identity_on_an_undeclared_type_or_field_is_refused_with_each_named()
    {
        Assert.Equal(
            [
                "Identity 'i' names 'Customer' as its second type, which is not a declared entity type.",
                "Identity 'i' matches 'erp', which is not a field of 'Material'.",
                "Identity 'i' matches 'sku', which is not a field of 'Item'.",
                "Identity 'i' is declared more than once.",
            ],
            [
                .. Refused(new IdentityDeclaration("i", "Material", "Customer", IdentityDecision.Automatic) { Match = [new FieldPair("erpCode", "code")] }),
                .. Refused(new IdentityDeclaration("i", "Material", "Item", IdentityDecision.Automatic) { Match = [new FieldPair("erp", "sku")] }),
                .. Refused(Named("i"), Named("i")),
            ]);
    }

    private static readonly EntityTypeDeclaration KeyedItem = new("KeyedItem", Item.System, Item.Table, Item.Key, Item.Fields) { Kinds = new Dictionary<string, FieldKind> { ["code"] = FieldKind.Key } };

    [Fact]
    public void Free_text_and_a_date_against_another_kind_are_no_values_to_match_on()
    {
        Assert.Contains(
            "'Material.note', which holds free text",
            Assert.Single(Refused(SameItem with { Match = [new FieldPair("note", "name")] })),
            StringComparison.Ordinal);
        Assert.Contains(
            "'Material.since' and 'KeyedItem.code', a date and a value that is not one",
            Assert.Single(Refused(KeyedItem, SameItem with { Second = "KeyedItem", Match = [new FieldPair("since", "code")] })),
            StringComparison.Ordinal);

        // A date against a field whose kind is not declared, or a field without a kind against a key, may hold the same value.
        Create(SameItem with { Match = [new FieldPair("since", "name")] });
        Create(KeyedItem, SameItem with { Second = "KeyedItem" });
    }

    [Fact]
    public void A_grouping_has_members_on_declared_fields_and_a_name_no_entity_type_has()
    {
        Assert.Equal(
            [
                "Grouping 'Line' has no member type; a group is nothing but its members.",
                "Grouping 'Line' has 'Cell' as a member, which is not a declared entity type.",
                "Grouping 'Line' groups 'WorkOrder' by 'cell', which is not one of its fields.",
                "Grouping 'Line' lists 'WorkOrder' as a member more than once.",
                "Grouping 'Item' has the name of an entity type; a group is a node of its own, so the two cannot share one.",
                "Grouping 'Line' is declared more than once.",
            ],
            [
                .. Refused(Line with { Members = [] }),
                .. Refused(Line with { Members = [new GroupMembership("Cell", "line")] }),
                .. Refused(Line with { Members = [new GroupMembership("WorkOrder", "cell")] }),
                .. Refused(Line with { Members = [.. Line.Members, .. Line.Members] }),
                .. Refused(new GroupingDeclaration("Item", Line.Members)),
                .. Refused(Line, Line),
            ]);
    }

    [Fact]
    public void A_grouping_of_several_types_groups_them_by_fields_that_can_hold_one_key()
    {
        var both = Line with { Members = [.. Line.Members, new GroupMembership("Material", "erpCode")] };
        Assert.Equal(both, Create(both).Groupings["Line"]);

        Assert.Contains(
            "groups by 'Material.note', which holds free text",
            Assert.Single(Refused(Line with { Members = [.. Line.Members, new GroupMembership("Material", "note")] })),
            StringComparison.Ordinal);
        Assert.Contains(
            "groups by 'Material.since' and 'KeyedItem.code', a date and a value that is not one",
            Assert.Single(Refused(KeyedItem, Line with { Members = [new GroupMembership("Material", "since"), new GroupMembership("KeyedItem", "code")] })),
            StringComparison.Ordinal);
    }

    /// <summary>D-86 ②: a path steps by name, so an identity cannot share one with a relation or an inverse — in either order of declaration.</summary>
    [Fact]
    public void An_identity_named_like_a_relation_or_an_inverse_is_refused()
    {
        var ordersItem = OntologyTests.OrdersItem with { Inverse = "ordered_by" };

        Assert.Contains("Identity 'orders_item' has the name of a relation or an inverse", Assert.Single(Refused(ordersItem, Named("orders_item"))), StringComparison.Ordinal);
        Assert.Contains("Identity 'ordered_by' has the name of a relation or an inverse", Assert.Single(Refused(ordersItem, Named("ordered_by"))), StringComparison.Ordinal);
    }

    /// <summary>D-86 ① ③: a path crosses an identity by its name from either of its types, to the other.</summary>
    [Fact]
    public void A_path_crosses_an_identity_from_either_type_to_the_other()
    {
        var ontology = Create(SameItem, OntologyTests.SuppliedBy);

        var fromMaterial = Assert.Single(ontology.Plan(new LookupCall(new EntityKey("Material", "M-1"), ["same_item"], ["name"])).Steps);
        var thenOn = ontology.Plan(new LookupCall(new EntityKey("Item", "I-100"), ["same_item"], ["note"]));
        var across = ontology.Plan(new LookupCall(new EntityKey("Material", "M-1"), ["same_item", "supplied_by"], ["name"]));

        Assert.Equal((SameItem, "same_item", "Material", "Item"), (fromMaterial.Identity, fromMaterial.Name, fromMaterial.Leaves, fromMaterial.Reaches));
        Assert.Null(fromMaterial.Relation);
        Assert.Equal(Material, thenOn.End);
        Assert.Equal("Supplier", across.End.Name);
    }

    [Fact]
    public void A_path_that_crosses_an_identity_from_neither_of_its_types_is_not_in_the_ontology_s_terms()
    {
        var ontology = Create(SameItem);

        var problem = Assert.Single(Assert.Throws<UnknownTermException>(() =>
            ontology.Plan(new LookupCall(new EntityKey("WorkOrder", "WO-1"), ["same_item"], ["quantity"]))).Problems);

        Assert.Equal("Path step 1: 'same_item' joins 'Material' and 'Item', but the walk is at 'WorkOrder'.", problem);
    }

    /// <summary>An identity of duplicates within one type is crossed back to that type.</summary>
    [Fact]
    public void A_path_crosses_an_identity_of_one_type_back_to_that_type()
    {
        var duplicates = new IdentityDeclaration("same_material", "Material", "Material", IdentityDecision.Confirmed) { Match = [new FieldPair("erpCode", "erpCode")] };

        var step = Assert.Single(Create(duplicates).Plan(new LookupCall(new EntityKey("Material", "M-1"), ["same_material"], ["note"])).Steps);

        Assert.Equal(("Material", "Material"), (step.Leaves, step.Reaches));
    }
}
