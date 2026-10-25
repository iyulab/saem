using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Saem.Console.Tests;

/// <summary>
/// saem D-88 · D-89: the catalog in force and its declaration graph, as every console role sees them — read from the
/// catalog file alone, no source touched; a catalog the ontology refuses is reported with its problems.
/// </summary>
public sealed class CatalogApiTests : IAsyncLifetime
{
    private const string Plant = """
        [
          { "kind": "entityType", "name": "Equipment", "system": "mes", "table": "equipment", "key": "equip_id", "fields": { "line": "line_id", "name": "name" } },
          { "kind": "entityType", "name": "Line", "system": "mes", "table": "lines", "key": "line_id", "fields": { "name": "name" } },
          { "kind": "entityType", "name": "Supplier", "system": "mes", "table": "suppliers", "key": "sup_id", "fields": { "name": "sup_name" } },
          { "kind": "entityType", "name": "Vendor", "system": "erp", "table": "vendors", "key": "vendor_id", "fields": { "name": "vendor_name" } },
          { "kind": "relation", "name": "on_line", "from": "Equipment", "via": "line", "to": "Line", "inverse": "equipment_on" },
          { "kind": "linkage", "name": "names", "compare": [{ "first": "name", "second": "name", "method": "jaroWinkler" }] },
          { "kind": "identity", "name": "same_supplier", "first": "Supplier", "second": "Vendor", "decision": "confirmed", "linkage": "names" },
          { "kind": "grouping", "name": "Fleet", "members": [{ "type": "Equipment", "by": "line" }] }
        ]
        """;

    private ConsoleServer _server = null!;

    public async ValueTask InitializeAsync() => _server = await ConsoleServer.StartAsync(ontologyJson: Plant);

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task The_catalog_is_its_version_and_its_declarations_in_the_files_own_shape_for_any_role()
    {
        using var auditor = _server.Client(ConsoleServer.Token("ann", ["privacy-office"]));

        var catalog = await auditor.GetFromJsonAsync<JsonElement>("api/catalog", TestContext.Current.CancellationToken);

        Assert.Equal(16, catalog.GetProperty("version").GetString()!.Length);
        Assert.Equal(
            ["entityType", "entityType", "entityType", "entityType", "relation", "identity", "linkage", "grouping"],
            catalog.GetProperty("declarations").EnumerateArray().Select(d => d.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task The_declaration_graph_has_a_node_per_type_and_grouping_sized_by_its_edges()
    {
        using var builder = _server.Client(ConsoleServer.Token("alice", ["ontology-builders"]));

        var graph = await builder.GetFromJsonAsync<DeclarationGraph>("api/catalog/graph", TestContext.Current.CancellationToken);

        Assert.Equal(
            ["Equipment entityType mes 2", "Line entityType mes 1", "Supplier entityType mes 1", "Vendor entityType erp 1", "Fleet grouping  1"],
            graph!.Nodes.Select(n => $"{n.Name} {n.Kind} {n.System} {n.Degree}"));
        Assert.Equal(
            ["Equipment>Line relation on_line via line inverse equipment_on", "Supplier>Vendor identity same_supplier linkage names", "Equipment>Fleet member Fleet via line"],
            graph.Edges.Select(e => $"{e.From}>{e.To} {e.Kind} {e.Name}" + (e.Via is { } v ? $" via {v}" : "") + (e.Inverse is { } i ? $" inverse {i}" : "") + (e.Linkage is { } l ? $" linkage {l}" : "")));
    }

    [Fact]
    public async Task A_catalog_the_ontology_refuses_is_reported_with_its_problems()
    {
        await File.WriteAllTextAsync(_server.OntologyFile, """[{ "kind": "relation", "name": "r", "from": "A", "via": "x", "to": "B" }]""", TestContext.Current.CancellationToken);
        using var builder = _server.Client(ConsoleServer.Token("alice", ["ontology-builders"]));

        using var response = await builder.GetAsync("api/catalog/graph", TestContext.Current.CancellationToken);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("'A', which is not a declared entity type", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }
}
