using Saem.Contracts;
using Xunit;

namespace Saem.Ontology.Tests;

/// <summary>
/// saem SD-12: the difference between two sets of declarations, sorted mechanically into changes that only add
/// vocabulary and changes that can break what an agent or a store built on the earlier set.
/// </summary>
public sealed class OntologyChangesTests
{
    private static readonly Ontology Before = Ontology.Create(
    [
        OntologyTests.WorkOrder,
        OntologyTests.Item,
        OntologyTests.Supplier,
        OntologyTests.OrdersItem with { Inverse = "ordered_by" },
        OntologyTests.SuppliedBy,
    ]);

    private static IReadOnlyList<string> Changes(params Declaration[] after) =>
        [.. OntologyChanges.Between(Before, Ontology.Create(after)).Select(c => $"{c.Kind} {c.Declaration}: {c.Change}")];

    /// <summary>SD-08: which rows a pass reads is no vocabulary — declaring, moving or dropping the change column only adds.</summary>
    [Fact]
    public void Declaring_or_dropping_the_column_rows_change_by_does_not_break()
    {
        var hinted = OntologyTests.WorkOrder with { ChangedAt = "updated_at" };
        var rest = new Declaration[] { OntologyTests.Item, OntologyTests.Supplier, OntologyTests.OrdersItem with { Inverse = "ordered_by" }, OntologyTests.SuppliedBy };

        Assert.Equal(["Additive entityType WorkOrder: tells its changed rows by updated_at"], Changes([hinted, .. rest]));
        Assert.Equal(["Additive entityType WorkOrder: is read whole by every pass"],
            [.. OntologyChanges.Between(Ontology.Create([hinted, .. rest]), Before).Select(c => $"{c.Kind} {c.Declaration}: {c.Change}")]);
    }

    [Fact]
    public void The_same_declarations_in_another_order_are_no_change()
    {
        Assert.Empty(Changes(OntologyTests.SuppliedBy, OntologyTests.OrdersItem with { Inverse = "ordered_by" }, OntologyTests.Supplier, OntologyTests.Item, OntologyTests.WorkOrder));
    }

    [Fact]
    public void New_types_relations_fields_inverses_and_kinds_only_add()
    {
        var workOrder = OntologyTests.WorkOrder with
        {
            Fields = new Dictionary<string, string>(OntologyTests.WorkOrder.Fields) { ["due"] = "due_date" },
            Kinds = new Dictionary<string, FieldKind> { ["quantity"] = FieldKind.Number },
        };

        Assert.Equal(
            [
                "Additive entityType Plant: added",
                "Additive entityType WorkOrder: field due added",
                "Additive entityType WorkOrder: field quantity declares its kind, Number",
                "Additive relation supplied_by: may be walked backwards as supplies",
            ],
            Changes(
                workOrder,
                OntologyTests.Item,
                OntologyTests.Supplier,
                new EntityTypeDeclaration("Plant", "erp", "plants", "plant_no", new Dictionary<string, string>()),
                OntologyTests.OrdersItem with { Inverse = "ordered_by" },
                OntologyTests.SuppliedBy with { Inverse = "supplies" }));
    }

    [Fact]
    public void Removing_moving_or_rekeying_vocabulary_breaks_and_comes_first()
    {
        var item = new EntityTypeDeclaration("Item", "erp", "items_v2", "sku", new Dictionary<string, string> { ["name"] = "item_title" })
        {
            KeyKind = KeyKind.Number,
            Record = ["doc_id"],
        };

        Assert.Equal(
            [
                "Breaking entityType Item: stands on erp.items_v2 instead of erp.items",
                "Breaking entityType Item: is keyed by sku instead of item_code",
                "Breaking entityType Item: compares its keys as numbers instead of text",
                "Breaking entityType Item: cites its records by doc_id instead of its key",
                "Breaking entityType Item: field name reads column item_title instead of item_name",
                "Breaking entityType Item: field supplier removed",
                "Breaking entityType Supplier: removed",
                "Breaking relation orders_item: may no longer be walked backwards as ordered_by",
                "Breaking relation supplied_by: removed",
                "Additive entityType Vendor: added",
            ],
            Changes(
                OntologyTests.WorkOrder,
                item,
                new EntityTypeDeclaration("Vendor", "erp", "suppliers", "supplier_id", new Dictionary<string, string> { ["name"] = "supplier_name" }),
                OntologyTests.OrdersItem));
    }

    [Fact]
    public void An_identity_changed_in_any_way_and_a_grouping_losing_or_regrouping_a_member_break()
    {
        var identity = new IdentityDeclaration("same", "WorkOrder", "WorkOrder", IdentityDecision.Automatic) { Match = [new FieldPair("item", "item")] };
        var grouping = new GroupingDeclaration("Batch", [new GroupMembership("WorkOrder", "item")]);
        var before = Ontology.Create([.. Before.EntityTypes.Values, .. Before.Relations.Values, identity, grouping]);
        var after = Ontology.Create(
        [
            .. Before.EntityTypes.Values,
            .. Before.Relations.Values,
            identity with { Decision = IdentityDecision.Confirmed },
            new GroupingDeclaration("Batch", [new GroupMembership("WorkOrder", "quantity"), new GroupMembership("Item", "name")]),
        ]);

        Assert.Equal(
            [
                new OntologyChange(ChangeKind.Breaking, "identity same", "tells what is one thing otherwise"),
                new OntologyChange(ChangeKind.Breaking, "grouping Batch", "groups WorkOrder by quantity instead of item"),
                new OntologyChange(ChangeKind.Additive, "grouping Batch", "has Item as a member, by name"),
            ],
            OntologyChanges.Between(before, after));
    }
}
