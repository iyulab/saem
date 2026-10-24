using Saem.Contracts;
using Xunit;

namespace Saem.Ontology.Tests;

/// <summary>
/// The rig's shape in miniature: a work order in the MES names an item by its code, and the ERP holds
/// that item and the supplier it comes from. No foreign key joins the two systems — the relation
/// <c>orders_item</c> exists only because it is declared.
/// </summary>
public class OntologyTests
{
    internal static readonly EntityTypeDeclaration WorkOrder = new(
        "WorkOrder", "mes", "work_orders", "wo_no",
        new Dictionary<string, string> { ["item"] = "item_code", ["quantity"] = "qty" });

    internal static readonly EntityTypeDeclaration Item = new(
        "Item", "erp", "items", "item_code",
        new Dictionary<string, string> { ["name"] = "item_name", ["supplier"] = "supplier_id" });

    internal static readonly EntityTypeDeclaration Supplier = new(
        "Supplier", "erp", "suppliers", "supplier_id",
        new Dictionary<string, string> { ["name"] = "supplier_name" });

    internal static readonly RelationDeclaration OrdersItem = new("orders_item", "WorkOrder", "item", "Item");

    internal static readonly RelationDeclaration SuppliedBy = new("supplied_by", "Item", "supplier", "Supplier");

    private static Ontology Rig() => Ontology.Create([WorkOrder, Item, Supplier, OrdersItem, SuppliedBy]);

    /// <summary>D-82 ②: a number key names one entity whatever its leading zeros or sign; a text key is itself.</summary>
    [Theory]
    [InlineData("0007", "7")]
    [InlineData("+7", "7")]
    [InlineData(" 7 ", "7")]
    [InlineData("-007", "-7")]
    [InlineData("7.0", "7.0")]
    [InlineData("A-7", "A-7")]
    public void A_number_key_is_spelled_as_the_integer_it_names(string key, string normalized)
    {
        Assert.Equal(normalized, (WorkOrder with { KeyKind = KeyKind.Number }).Normalize(key));
        Assert.Equal(key, WorkOrder.Normalize(key));
        Assert.Equal(key, (WorkOrder with { KeyKind = KeyKind.Text }).Normalize(key));
    }

    /// <summary>D-82 ①: a field's kind is declared for a field the type has; a kind for no such field is refused.</summary>
    [Fact]
    public void A_kind_declared_for_a_field_the_type_does_not_have_is_refused()
    {
        var thrown = Assert.Throws<OntologyDeclarationException>(() => Ontology.Create(
            [WorkOrder with { Kinds = new Dictionary<string, FieldKind> { ["item"] = FieldKind.Key, ["salary"] = FieldKind.Number } }]));

        Assert.Contains("declares the kind of 'salary', which is not one of its fields", Assert.Single(thrown.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_change_to_a_key_kind_changes_the_ontology_version()
    {
        Assert.NotEqual(Rig().Version, Ontology.Create([WorkOrder with { KeyKind = KeyKind.Number }, Item, Supplier, OrdersItem, SuppliedBy]).Version);
        Assert.Equal(Rig().Version, Ontology.Create([SuppliedBy, OrdersItem, Supplier, Item, WorkOrder]).Version);
    }

    [Fact]
    public void A_walk_across_two_systems_is_planned_from_declarations_alone()
    {
        var plan = Rig().Plan(new LookupCall(new EntityKey("WorkOrder", "WO-1"), ["orders_item", "supplied_by"], ["name"]));

        Assert.Equal(WorkOrder, plan.Start);
        Assert.Equal([OrdersItem, SuppliedBy], plan.Steps);
        Assert.Equal(Supplier, plan.End);
        Assert.Equal("mes", plan.Start.System);
        Assert.Equal("erp", plan.End.System);
        Assert.Equal(["name"], plan.Read);
    }

    [Fact]
    public void An_empty_path_reads_the_start_entity_itself()
    {
        var plan = Rig().Plan(new LookupCall(new EntityKey("Item", "I-9"), [], ["name", "supplier"]));

        Assert.Empty(plan.Steps);
        Assert.Equal(Item, plan.End);
    }

    [Fact]
    public void A_relation_followed_from_the_wrong_type_is_named_with_where_the_walk_is()
    {
        var e = Assert.Throws<UnknownTermException>(() =>
            Rig().Plan(new LookupCall(new EntityKey("WorkOrder", "WO-1"), ["supplied_by"], ["name"])));

        Assert.Equal(["Path step 1: 'supplied_by' leaves 'Item', but the walk is at 'WorkOrder'."], e.Problems);
    }

    [Fact]
    public void Unknown_terms_are_refused_as_a_malformed_question_not_answered()
    {
        var ontology = Rig();

        Assert.Equal(
            ["'Customer' is not a declared entity type."],
            Assert.Throws<UnknownTermException>(() => ontology.Plan(new LookupCall(new EntityKey("Customer", "C-1"), [], ["name"]))).Problems);
        Assert.Equal(
            ["Path step 2: 'made_by' is not a declared relation."],
            Assert.Throws<UnknownTermException>(() => ontology.Plan(new LookupCall(new EntityKey("WorkOrder", "WO-1"), ["orders_item", "made_by"], ["name"]))).Problems);
        Assert.Equal(
            ["'price' is not a field of 'Supplier'.", "'country' is not a field of 'Supplier'."],
            Assert.Throws<UnknownTermException>(() => ontology.Plan(new LookupCall(new EntityKey("Item", "I-9"), ["supplied_by"], ["name", "price", "country"]))).Problems);
    }

    [Fact]
    public void A_lookup_that_reads_nothing_is_malformed()
    {
        Assert.Equal(
            ["The lookup reads no field; an answer states at least one."],
            Assert.Throws<UnknownTermException>(() => Rig().Plan(new LookupCall(new EntityKey("Item", "I-9"), [], []))).Problems);
    }

    [Fact]
    public void A_relation_walked_backwards_ends_the_plan_at_the_type_it_leaves()
    {
        var ontology = Ontology.Create([WorkOrder, Item, Supplier, OrdersItem with { Inverse = "ordered_by" }, SuppliedBy]);

        var plan = ontology.Plan(new LookupCall(new EntityKey("Item", "I-100"), ["ordered_by"], ["quantity"]));

        Assert.Equal(Item, plan.Start);
        Assert.Empty(plan.Steps);
        Assert.Equal("orders_item", plan.Inverse!.Name);
        Assert.Equal(WorkOrder, plan.End);
        Assert.Equal("orders_item", ontology.Inverses["ordered_by"].Name);
    }

    [Fact]
    public void A_relation_walked_backwards_is_the_last_step_and_leaves_the_type_it_led_to()
    {
        var ontology = Ontology.Create([WorkOrder, Item, Supplier, OrdersItem with { Inverse = "ordered_by" }, SuppliedBy]);

        var notLast = Assert.Throws<UnknownTermException>(() => ontology.Plan(new LookupCall(new EntityKey("Item", "I-100"), ["ordered_by", "orders_item"], ["name"])));
        var wrongType = Assert.Throws<UnknownTermException>(() => ontology.Plan(new LookupCall(new EntityKey("Supplier", "S-1"), ["ordered_by"], ["quantity"])));

        Assert.Contains("only the last step", Assert.Single(notLast.Problems), StringComparison.Ordinal);
        Assert.Contains("leaves 'Item', but the walk is at 'Supplier'", Assert.Single(wrongType.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void An_inverse_named_like_a_relation_or_another_inverse_is_refused()
    {
        var problems = Assert.Throws<OntologyDeclarationException>(() => Ontology.Create(
            [WorkOrder, Item, Supplier, OrdersItem with { Inverse = "supplied_by" }, SuppliedBy with { Inverse = "  " }])).Problems;

        Assert.Equal(2, problems.Count);
    }

    [Fact]
    public void Inconsistent_declarations_are_refused_whole_with_every_problem_named()
    {
        var e = Assert.Throws<OntologyDeclarationException>(() => Ontology.Create(
        [
            WorkOrder,
            Item,
            Item,
            new RelationDeclaration("orders_item", "WorkOrder", "item", "Item"),
            new RelationDeclaration("orders_item", "WorkOrder", "item", "Item"),
            new RelationDeclaration("supplied_by", "Item", "vendor", "Supplier"),
            new EntityTypeDeclaration("Blank", "erp", " ", "id", new Dictionary<string, string>()),
            new EntityTypeDeclaration("Unnamed", "forms", "inspections", "inspection_no", new Dictionary<string, string>(), ["fb_doc_id", " "]),
        ]));

        Assert.Equal(
            [
                "Entity type 'Item' is declared more than once.",
                "Entity type 'Blank' leaves its name, system, table or key empty.",
                "Entity type 'Unnamed' declares its record by no column or an empty one.",
                "Relation 'orders_item' is declared more than once.",
                "Relation 'supplied_by' leaves 'Item' through 'vendor', which is not one of its fields.",
                "Relation 'supplied_by' leads to 'Supplier', which is not a declared entity type.",
            ],
            e.Problems);
    }
}
