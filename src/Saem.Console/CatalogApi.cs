using System.Text.Json;
using Saem.Ontology;

namespace Saem.Console;

/// <summary>Where the console reads the declarations in force: the same catalog file the MCP server reads.</summary>
public sealed class ConsoleCatalogOptions
{
    /// <summary>The catalog file.</summary>
    public string Ontology { get; set; } = "";

    /// <summary>
    /// The draft a builder confirms into the catalog (empty: the console confirms nothing). The file a catalog is drafted
    /// into (<c>saem-author --draft</c>) — reviewed, then confirmed here.
    /// </summary>
    public string Draft { get; set; } = "";

    /// <summary>The file each confirmation is recorded in, one JSON line each (required with <see cref="Draft"/>).</summary>
    public string Confirmations { get; set; } = "";

    /// <summary>
    /// The proposals of the pass that wrote <see cref="Draft"/> (<c>saem-author --proposals</c>); with it a builder reviews
    /// the draft proposal by proposal in the console. Empty: the console shows and confirms the draft as a whole only.
    /// </summary>
    public string Proposals { get; set; } = "";
}

/// <summary>
/// The declarations in force, as builders read them (saem D-88): the catalog itself, and the declaration graph —
/// types and groupings as nodes, relations, identities and memberships as edges. Declarations are the organization's
/// own vocabulary (D-37 ①), so every person with a console role sees the same; no source is read for either.
/// </summary>
internal static class CatalogApi
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/catalog", async (IOntologyCatalog catalog, CancellationToken ct) =>
            await ReadAsync(catalog, ontology => Results.Text(View(ontology), "application/json"), ct));

        api.MapGet("/catalog/graph", async (IOntologyCatalog catalog, CancellationToken ct) =>
            await ReadAsync(catalog, ontology => TypedResults.Ok(Graph(ontology)), ct));
    }

    /// <summary>
    /// Every declaration, kind by kind in the order a catalog file lists them, each kind by name — the shape a catalog
    /// file holds, so what the console shows is what a builder would write.
    /// </summary>
    internal static IReadOnlyList<Declaration> Declarations(Ontology.Ontology ontology) =>
    [
        .. ontology.EntityTypes.Values.OrderBy(t => t.Name, StringComparer.Ordinal),
        .. ontology.Relations.Values.OrderBy(r => r.Name, StringComparer.Ordinal),
        .. ontology.Identities.Values.OrderBy(i => i.Name, StringComparer.Ordinal),
        .. ontology.Linkages.Values.OrderBy(l => l.Name, StringComparer.Ordinal),
        .. ontology.Groupings.Values.OrderBy(g => g.Name, StringComparer.Ordinal),
    ];

    /// <summary>The declaration graph: a node per entity type and grouping, an edge per relation, identity and membership.</summary>
    internal static DeclarationGraph Graph(Ontology.Ontology ontology)
    {
        var edges = new List<GraphEdge>();
        edges.AddRange(ontology.Relations.Values.OrderBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => new GraphEdge(r.From, r.To, GraphEdgeKinds.Relation, r.Name) { Via = r.Via, Inverse = r.Inverse }));
        edges.AddRange(ontology.Identities.Values.OrderBy(i => i.Name, StringComparer.Ordinal)
            .Select(i => new GraphEdge(i.First, i.Second, GraphEdgeKinds.Identity, i.Name) { Linkage = i.Linkage }));
        edges.AddRange(ontology.Groupings.Values.OrderBy(g => g.Name, StringComparer.Ordinal)
            .SelectMany(g => g.Members.Select(m => new GraphEdge(m.Type, g.Name, GraphEdgeKinds.Member, g.Name) { Via = m.By })));

        // A node's degree is how many edges touch it — what a graph view sizes it by; a self-edge touches it once.
        var degree = edges.SelectMany(e => e.From == e.To ? [e.From] : new[] { e.From, e.To })
            .GroupBy(n => n, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var nodes = ontology.EntityTypes.Values.OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new GraphNode(t.Name, GraphNodeKinds.EntityType, t.System, degree.GetValueOrDefault(t.Name)))
            .Concat(ontology.Groupings.Values.OrderBy(g => g.Name, StringComparer.Ordinal)
                .Select(g => new GraphNode(g.Name, GraphNodeKinds.Grouping, null, degree.GetValueOrDefault(g.Name))))
            .ToList();
        return new DeclarationGraph(ontology.Version, nodes, edges);
    }

    private static async Task<IResult> ReadAsync(IOntologyCatalog catalog, Func<Ontology.Ontology, IResult> show, CancellationToken ct)
    {
        try
        {
            return show(await catalog.GetAsync(ct));
        }
        catch (OntologyDeclarationException e)
        {
            // The catalog in force is refused whole; say why, so a builder knows what to fix.
            return TypedResults.Problem(
                title: "The catalog is refused.",
                detail: string.Join(" ", e.Problems),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// <c>{ "version": …, "declarations": [ … ] }</c> — the declarations written by the catalog's own serializer, so each
    /// carries its <c>kind</c> and reads exactly as a catalog file would.
    /// </summary>
    private static string View(Ontology.Ontology ontology)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("version", ontology.Version);
            writer.WritePropertyName("declarations");
            JsonSerializer.Serialize(writer, Declarations(ontology), OntologyJson.Default.IReadOnlyListDeclaration);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>The declaration graph of one catalog version.</summary>
/// <param name="Version">The catalog version it was drawn from.</param>
/// <param name="Nodes">Entity types and groupings.</param>
/// <param name="Edges">Relations, identities and group memberships.</param>
public sealed record DeclarationGraph(string Version, IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges);

/// <summary>A node: an entity type (with the system it stands on) or a grouping (computed from its members — no system).</summary>
public sealed record GraphNode(string Name, string Kind, string? System, int Degree);

/// <summary>
/// An edge: a relation from a type to the type its field names, an identity between two types (no direction), or a
/// type's membership in a grouping.
/// </summary>
public sealed record GraphEdge(string From, string To, string Kind, string Name)
{
    /// <summary>For a relation, the field it leaves through; for a membership, the field naming the group.</summary>
    public string? Via { get; init; }

    /// <summary>For a relation walked backwards too, its inverse's name.</summary>
    public string? Inverse { get; init; }

    /// <summary>For an identity by a linkage rule, the rule.</summary>
    public string? Linkage { get; init; }
}

/// <summary>The kinds of node.</summary>
public static class GraphNodeKinds
{
    public const string EntityType = "entityType";
    public const string Grouping = "grouping";
}

/// <summary>The kinds of edge.</summary>
public static class GraphEdgeKinds
{
    public const string Relation = "relation";
    public const string Identity = "identity";
    public const string Member = "member";
}
