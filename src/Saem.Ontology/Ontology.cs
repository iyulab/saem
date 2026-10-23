using Saem.Contracts;

namespace Saem.Ontology;

/// <summary>
/// A consistent set of declarations: every name unique among its kind, every relation starting and
/// ending at a declared type and leaving through a declared field. Built only through
/// <see cref="Create"/>, which reports every inconsistency at once — a catalog with one mistake is
/// refused whole, so a walk never runs on half an ontology.
/// </summary>
public sealed class Ontology
{
    private readonly Dictionary<string, EntityTypeDeclaration> _types;
    private readonly Dictionary<string, RelationDeclaration> _relations;
    private readonly Dictionary<string, RelationDeclaration> _inverses;

    private Ontology(Dictionary<string, EntityTypeDeclaration> types, Dictionary<string, RelationDeclaration> relations)
    {
        _types = types;
        _relations = relations;
        _inverses = relations.Values.Where(r => r.Inverse is not null).ToDictionary(r => r.Inverse!, StringComparer.Ordinal);
    }

    /// <summary>The declared entity types, by name.</summary>
    public IReadOnlyDictionary<string, EntityTypeDeclaration> EntityTypes => _types;

    /// <summary>The declared relations, by name.</summary>
    public IReadOnlyDictionary<string, RelationDeclaration> Relations => _relations;

    /// <summary>The relations that may be walked backwards, by the name of the inverse.</summary>
    public IReadOnlyDictionary<string, RelationDeclaration> Inverses => _inverses;

    /// <summary>Builds an ontology from declarations, or refuses them with every problem named.</summary>
    /// <exception cref="OntologyDeclarationException">The declarations are not consistent.</exception>
    public static Ontology Create(IEnumerable<Declaration> declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);

        var problems = new List<string>();
        var types = new Dictionary<string, EntityTypeDeclaration>(StringComparer.Ordinal);
        var relations = new Dictionary<string, RelationDeclaration>(StringComparer.Ordinal);
        var all = declarations.ToList();

        foreach (var type in all.OfType<EntityTypeDeclaration>())
        {
            if (Blank(type.Name, type.System, type.Table, type.Key))
            {
                problems.Add($"Entity type '{type.Name}' leaves its name, system, table or key empty.");
                continue;
            }

            if (type.Fields is null || type.Fields.Any(f => string.IsNullOrWhiteSpace(f.Key) || string.IsNullOrWhiteSpace(f.Value)))
            {
                problems.Add($"Entity type '{type.Name}' has a field without a name or a column.");
                continue;
            }

            if (type.Record is not null && (type.Record.Count == 0 || type.Record.Any(string.IsNullOrWhiteSpace)))
            {
                problems.Add($"Entity type '{type.Name}' declares its record by no column or an empty one.");
                continue;
            }

            if (!types.TryAdd(type.Name, type))
            {
                problems.Add($"Entity type '{type.Name}' is declared more than once.");
            }
        }

        foreach (var relation in all.OfType<RelationDeclaration>())
        {
            if (Blank(relation.Name, relation.From, relation.Via, relation.To))
            {
                problems.Add($"Relation '{relation.Name}' leaves its name, from, via or to empty.");
                continue;
            }

            if (!relations.TryAdd(relation.Name, relation))
            {
                problems.Add($"Relation '{relation.Name}' is declared more than once.");
                continue;
            }

            if (!types.TryGetValue(relation.From, out var from))
            {
                problems.Add($"Relation '{relation.Name}' starts at '{relation.From}', which is not a declared entity type.");
            }
            else if (!from.Fields.ContainsKey(relation.Via))
            {
                problems.Add($"Relation '{relation.Name}' leaves '{relation.From}' through '{relation.Via}', which is not one of its fields.");
            }

            if (!types.ContainsKey(relation.To))
            {
                problems.Add($"Relation '{relation.Name}' leads to '{relation.To}', which is not a declared entity type.");
            }
        }

        // An inverse is walked by its name, so it shares the relations' names: one name, one walk.
        var inverses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relation in relations.Values.Where(r => r.Inverse is not null))
        {
            if (string.IsNullOrWhiteSpace(relation.Inverse))
            {
                problems.Add($"Relation '{relation.Name}' names an empty inverse.");
            }
            else if (relations.ContainsKey(relation.Inverse) || !inverses.Add(relation.Inverse))
            {
                problems.Add($"Relation '{relation.Name}' names its inverse '{relation.Inverse}', which is already the name of a relation or another inverse.");
            }
        }

        if (problems.Count > 0)
        {
            throw new OntologyDeclarationException(problems);
        }

        return new Ontology(types, relations);
    }

    /// <summary>
    /// Reads a lookup in the ontology's terms: the type it starts at, the relations it follows — each
    /// leaving the type the previous one led to — and the fields it reads at the end. Says nothing
    /// about whether any entity exists; that is the sources' to answer, under the person's own access.
    /// </summary>
    /// <exception cref="UnknownTermException">The lookup names something the ontology does not declare, or walks a relation from the wrong type.</exception>
    public LookupPlan Plan(LookupCall call)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (!_types.TryGetValue(call.Start.Type, out var start))
        {
            throw new UnknownTermException([$"'{call.Start.Type}' is not a declared entity type."]);
        }

        var problems = new List<string>();
        var steps = new List<RelationDeclaration>();
        RelationDeclaration? inverse = null;
        var at = start;
        for (var i = 0; i < call.Path.Count; i++)
        {
            var name = call.Path[i];
            if (_inverses.TryGetValue(name, out var walkedBack))
            {
                if (i != call.Path.Count - 1)
                {
                    // Backwards, a relation leads to many entities; a walk goes on from one.
                    problems.Add($"Path step {i + 1}: '{name}' walks '{walkedBack.Name}' backwards, which only the last step of a path may do.");
                    break;
                }

                if (!string.Equals(walkedBack.To, at.Name, StringComparison.Ordinal))
                {
                    problems.Add($"Path step {i + 1}: '{name}' leaves '{walkedBack.To}', but the walk is at '{at.Name}'.");
                    break;
                }

                inverse = walkedBack;
                at = _types[walkedBack.From];
                break;
            }

            if (!_relations.TryGetValue(name, out var relation))
            {
                problems.Add($"Path step {i + 1}: '{name}' is not a declared relation.");
                break;
            }

            if (!string.Equals(relation.From, at.Name, StringComparison.Ordinal))
            {
                problems.Add($"Path step {i + 1}: '{name}' leaves '{relation.From}', but the walk is at '{at.Name}'.");
                break;
            }

            steps.Add(relation);
            at = _types[relation.To];
        }

        if (problems.Count == 0 && call.Read.Count == 0)
        {
            problems.Add("The lookup reads no field; an answer states at least one.");
        }

        if (problems.Count == 0)
        {
            problems.AddRange(call.Read
                .Where(field => !at.Fields.ContainsKey(field))
                .Select(field => $"'{field}' is not a field of '{at.Name}'."));
        }

        if (problems.Count > 0)
        {
            throw new UnknownTermException(problems);
        }

        return new LookupPlan(start, steps, at, call.Read) { Inverse = inverse };
    }

    private static bool Blank(params string?[] values) => values.Any(string.IsNullOrWhiteSpace);
}

/// <summary>A lookup read in the ontology's terms — what to query, in which system, in which order.</summary>
/// <param name="Start">The type the walk starts at.</param>
/// <param name="Steps">The relations followed, in order.</param>
/// <param name="End">The type the walk ends at — the start type when there are no steps.</param>
/// <param name="Read">The fields read on the end type, each one of its declared fields.</param>
public sealed record LookupPlan(
    EntityTypeDeclaration Start,
    IReadOnlyList<RelationDeclaration> Steps,
    EntityTypeDeclaration End,
    IReadOnlyList<string> Read)
{
    /// <summary>
    /// The relation the last step walks backwards — from the entity <see cref="Steps"/> reach to every
    /// entity of <see cref="End"/> that leads to it — or null when every step walks forwards.
    /// </summary>
    public RelationDeclaration? Inverse { get; init; }
}
