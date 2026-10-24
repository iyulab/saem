using Saem.Contracts;
using System.Text.Json;
using Xunit;

namespace Saem.Ontology.Tests;

/// <summary>
/// The file adapter: a JSON array of declarations, each naming its kind, read through the same
/// consistency rules as declarations built in memory — and re-read on every call.
/// </summary>
public sealed class FileOntologyCatalogTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"saem-ontology-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_path);

    private const string RigJson = """
        [
          { "kind": "entityType", "name": "WorkOrder", "system": "mes", "table": "work_orders", "key": "wo_no",
            "fields": { "item": "item_code", "quantity": "qty" } },
          { "name": "Item", "system": "erp", "table": "items", "key": "item_code",
            "fields": { "name": "item_name", "supplier": "supplier_id" }, "kind": "entityType" },
          { "kind": "entityType", "name": "Supplier", "system": "erp", "table": "suppliers", "key": "supplier_id",
            "fields": { "name": "supplier_name" } },
          { "kind": "relation", "name": "orders_item", "from": "WorkOrder", "via": "item", "to": "Item", "inverse": "ordered_by" },
          { "kind": "relation", "name": "supplied_by", "from": "Item", "via": "supplier", "to": "Supplier" }
        ]
        """;

    [Fact]
    public async Task Declarations_read_from_the_file_are_the_ones_declared_in_memory()
    {
        await File.WriteAllTextAsync(_path, RigJson, TestContext.Current.CancellationToken);

        var ontology = await new FileOntologyCatalog(_path).GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["WorkOrder", "Item", "Supplier"], ontology.EntityTypes.Keys);
        Assert.Equal(OntologyTests.WorkOrder.Fields, ontology.EntityTypes["WorkOrder"].Fields);
        Assert.Equal(OntologyTests.Item with { Fields = ontology.EntityTypes["Item"].Fields }, ontology.EntityTypes["Item"]);
        Assert.Equal(OntologyTests.SuppliedBy, ontology.Relations["supplied_by"]);
        Assert.Equal(OntologyTests.OrdersItem with { Inverse = "ordered_by" }, ontology.Relations["orders_item"]);
    }

    [Fact]
    public async Task A_declared_record_is_read_from_the_file_and_written_back_only_where_declared()
    {
        const string json = """
            [
              { "kind": "entityType", "name": "Inspection", "system": "forms", "table": "inspections", "key": "inspection_no",
                "fields": { "finding": "finding" }, "record": [ "fb_doc_id" ] }
            ]
            """;
        await File.WriteAllTextAsync(_path, json, TestContext.Current.CancellationToken);

        var inspection = (await new FileOntologyCatalog(_path).GetAsync(TestContext.Current.CancellationToken)).EntityTypes["Inspection"];

        Assert.Equal(["fb_doc_id"], inspection.Record);
        Assert.Contains("\"record\":[\"fb_doc_id\"]", JsonSerializer.Serialize<Declaration>(inspection, OntologyJson.Default.Options), StringComparison.Ordinal);
        Assert.DoesNotContain("record", JsonSerializer.Serialize<Declaration>(OntologyTests.Item, OntologyJson.Default.Options), StringComparison.Ordinal);
    }

    /// <summary>D-82: a key's kind and the fields' kinds are read from the file by name, and left out where undeclared.</summary>
    [Fact]
    public async Task Key_and_field_kinds_are_read_from_the_file_by_name()
    {
        await File.WriteAllTextAsync(_path, """
            [
              { "kind": "entityType", "name": "Part", "system": "mes", "table": "parts", "key": "part_no", "keyKind": "number",
                "fields": { "name": "part_name", "note": "remark" }, "kinds": { "note": "freeText" } }
            ]
            """, TestContext.Current.CancellationToken);

        var part = (await new FileOntologyCatalog(_path).GetAsync(TestContext.Current.CancellationToken)).EntityTypes["Part"];

        Assert.Equal(KeyKind.Number, part.KeyKind);
        Assert.Equal(FieldKind.FreeText, part.Kinds!["note"]);
        Assert.False(part.Kinds.ContainsKey("name"));
        var written = System.Text.Json.JsonSerializer.Serialize<IReadOnlyList<Declaration>>([part with { KeyKind = null, Kinds = null }], OntologyJson.Default.IReadOnlyListDeclaration);
        Assert.DoesNotContain("kind\":\"number", written, StringComparison.Ordinal);
        Assert.DoesNotContain("kinds", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edit_to_the_file_is_seen_by_the_next_read()
    {
        var catalog = new FileOntologyCatalog(_path);
        await File.WriteAllTextAsync(_path, RigJson, TestContext.Current.CancellationToken);
        Assert.Equal(2, (await catalog.GetAsync(TestContext.Current.CancellationToken)).Relations.Count);

        const string last = """{ "kind": "relation", "name": "supplied_by", "from": "Item", "via": "supplier", "to": "Supplier" }""";
        const string added = """, { "kind": "relation", "name": "orders_same_item", "from": "WorkOrder", "via": "item", "to": "Item" }""";
        await File.WriteAllTextAsync(_path, RigJson.Replace(last, last + added, StringComparison.Ordinal), TestContext.Current.CancellationToken);

        Assert.Equal(3, (await catalog.GetAsync(TestContext.Current.CancellationToken)).Relations.Count);
    }

    [Theory]
    [InlineData("""[ { "name": "X", "system": "a", "table": "t", "key": "k", "fields": {} } ]""")]
    [InlineData("""[ { "kind": "view", "name": "X" } ]""")]
    [InlineData("""{ "kind": "entityType" }""")]
    [InlineData("""null""")]
    [InlineData("""not json""")]
    public async Task A_file_that_is_not_an_array_of_declarations_is_refused_naming_the_file(string json)
    {
        await File.WriteAllTextAsync(_path, json, TestContext.Current.CancellationToken);

        var e = await Assert.ThrowsAsync<OntologyDeclarationException>(
            async () => await new FileOntologyCatalog(_path).GetAsync(TestContext.Current.CancellationToken));

        Assert.Contains(_path, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_file_is_refused_naming_the_file()
    {
        var e = await Assert.ThrowsAsync<OntologyDeclarationException>(
            async () => await new FileOntologyCatalog(_path).GetAsync(TestContext.Current.CancellationToken));

        Assert.Contains(_path, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_inconsistent_file_is_refused_with_its_problems()
    {
        await File.WriteAllTextAsync(
            _path,
            """[ { "kind": "relation", "name": "r", "from": "A", "via": "f", "to": "B" } ]""",
            TestContext.Current.CancellationToken);

        var e = await Assert.ThrowsAsync<OntologyDeclarationException>(
            async () => await new FileOntologyCatalog(_path).GetAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            ["Relation 'r' starts at 'A', which is not a declared entity type.", "Relation 'r' leads to 'B', which is not a declared entity type."],
            e.Problems);
    }

    [Fact]
    public async Task An_identity_and_a_grouping_are_read_from_the_file_and_a_grouping_carrying_fields_is_refused()
    {
        const string json = """
            [
              { "kind": "entityType", "name": "WorkOrder", "system": "mes", "table": "work_orders", "key": "wo_no",
                "fields": { "item": "item_code", "line": "line_code" } },
              { "kind": "entityType", "name": "Item", "system": "erp", "table": "items", "key": "item_code",
                "fields": { "code": "item_code" } },
              { "kind": "identity", "name": "same_item", "first": "WorkOrder", "second": "Item", "decision": "confirmed",
                "match": [ { "first": "item", "second": "code" } ] },
              { "kind": "grouping", "name": "Line", "members": [ { "type": "WorkOrder", "by": "line" } ] }
            ]
            """;
        await File.WriteAllTextAsync(_path, json, TestContext.Current.CancellationToken);

        var ontology = await new FileOntologyCatalog(_path).GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(IdentityDecision.Confirmed, ontology.Identities["same_item"].Decision);
        Assert.Equal(new FieldPair("item", "code"), Assert.Single(ontology.Identities["same_item"].Match!));
        Assert.Equal(new GroupMembership("WorkOrder", "line"), Assert.Single(ontology.Groupings["Line"].Members));

        // A group is its members: a value written on the group itself has nowhere to go, so the file is refused, not trimmed.
        await File.WriteAllTextAsync(
            _path,
            json.Replace("\"members\": [", "\"fields\": { \"capacity\": \"cap\" }, \"members\": [", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        var refused = await Assert.ThrowsAsync<OntologyDeclarationException>(() => new FileOntologyCatalog(_path).GetAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("fields", refused.Message, StringComparison.Ordinal);
    }
}
