using System.Security.Cryptography;
using System.Text.Json;
using Saem.Contracts;

namespace Saem.Ontology;

/// <summary>
/// A consistent set of declarations: every name unique among its kind — and a path's steps, relations, inverses and
/// identities, unique among them all — every relation starting and
/// ending at a declared type and leaving through a declared field, every identity and grouping standing on
/// declared types and fields, every linkage rule an identity names declared and comparing fields its two types have. Built only through
/// <see cref="Create"/>, which reports every inconsistency at once — a catalog with one mistake is
/// refused whole, so a walk never runs on half an ontology.
/// </summary>
public sealed class Ontology
{
    private readonly Dictionary<string, EntityTypeDeclaration> _types;
    private readonly Dictionary<string, RelationDeclaration> _relations;
    private readonly Dictionary<string, RelationDeclaration> _inverses;
    private readonly Dictionary<string, IdentityDeclaration> _identities;
    private readonly Dictionary<string, LinkageDeclaration> _linkages;
    private readonly Dictionary<string, GroupingDeclaration> _groupings;

    private Ontology(
        Dictionary<string, EntityTypeDeclaration> types,
        Dictionary<string, RelationDeclaration> relations,
        Dictionary<string, IdentityDeclaration> identities,
        Dictionary<string, LinkageDeclaration> linkages,
        Dictionary<string, GroupingDeclaration> groupings)
    {
        _types = types;
        _relations = relations;
        _identities = identities;
        _linkages = linkages;
        _groupings = groupings;
        _inverses = relations.Values.Where(r => r.Inverse is not null).ToDictionary(r => r.Inverse!, StringComparer.Ordinal);
        Version = VersionOf([
            .. types.Values.OrderBy(t => t.Name, StringComparer.Ordinal),
            .. relations.Values.OrderBy(r => r.Name, StringComparer.Ordinal),
            .. identities.Values.OrderBy(i => i.Name, StringComparer.Ordinal),
            .. linkages.Values.OrderBy(l => l.Name, StringComparer.Ordinal),
            .. groupings.Values.OrderBy(g => g.Name, StringComparer.Ordinal),
        ]);
    }

    /// <summary>
    /// Which declarations these are: a hash of their content, the same for the same declarations in any order
    /// and different the moment one changes. An answer carries it, so it says which vocabulary it was walked in.
    /// </summary>
    public string Version { get; }

    /// <summary>The declared entity types, by name.</summary>
    public IReadOnlyDictionary<string, EntityTypeDeclaration> EntityTypes => _types;

    /// <summary>The declared relations, by name.</summary>
    public IReadOnlyDictionary<string, RelationDeclaration> Relations => _relations;

    /// <summary>The relations that may be walked backwards, by the name of the inverse.</summary>
    public IReadOnlyDictionary<string, RelationDeclaration> Inverses => _inverses;

    /// <summary>The declared identities, by name.</summary>
    public IReadOnlyDictionary<string, IdentityDeclaration> Identities => _identities;

    /// <summary>The declared linkage rules, by name.</summary>
    public IReadOnlyDictionary<string, LinkageDeclaration> Linkages => _linkages;

    /// <summary>The declared groupings, by name.</summary>
    public IReadOnlyDictionary<string, GroupingDeclaration> Groupings => _groupings;

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

            if (type.ChangedAt is not null && string.IsNullOrWhiteSpace(type.ChangedAt))
            {
                problems.Add($"Entity type '{type.Name}' declares an empty column for when its rows change.");
                continue;
            }

            if (type.Kinds?.Keys.Where(field => !type.Fields.ContainsKey(field)).ToList() is { Count: > 0 } undeclared)
            {
                problems.Add($"Entity type '{type.Name}' declares the kind of {string.Join(", ", undeclared.Select(f => $"'{f}'"))}, which is not one of its fields.");
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

        var linkages = new Dictionary<string, LinkageDeclaration>(StringComparer.Ordinal);
        foreach (var linkage in all.OfType<LinkageDeclaration>())
        {
            if (Check(linkage, problems) && !linkages.TryAdd(linkage.Name, linkage))
            {
                problems.Add($"Linkage rule '{linkage.Name}' is declared more than once.");
            }
        }

        var identities = new Dictionary<string, IdentityDeclaration>(StringComparer.Ordinal);
        foreach (var identity in all.OfType<IdentityDeclaration>())
        {
            if (!Check(identity, types, linkages, problems))
            {
                continue;
            }

            // A path crosses an identity by its name, as it walks a relation by its own: one name, one step.
            if (relations.ContainsKey(identity.Name) || inverses.Contains(identity.Name))
            {
                problems.Add($"Identity '{identity.Name}' has the name of a relation or an inverse; a path steps by the name, so the two cannot share one.");
            }
            else if (!identities.TryAdd(identity.Name, identity))
            {
                problems.Add($"Identity '{identity.Name}' is declared more than once.");
            }
        }

        var groupings = new Dictionary<string, GroupingDeclaration>(StringComparer.Ordinal);
        foreach (var grouping in all.OfType<GroupingDeclaration>())
        {
            if (!Check(grouping, types, problems))
            {
                continue;
            }

            if (types.ContainsKey(grouping.Name))
            {
                problems.Add($"Grouping '{grouping.Name}' has the name of an entity type; a group is a node of its own, so the two cannot share one.");
            }
            else if (!groupings.TryAdd(grouping.Name, grouping))
            {
                problems.Add($"Grouping '{grouping.Name}' is declared more than once.");
            }
        }

        if (problems.Count > 0)
        {
            throw new OntologyDeclarationException(problems);
        }

        return new Ontology(types, relations, identities, linkages, groupings);
    }

    private static bool Check(
        IdentityDeclaration identity,
        Dictionary<string, EntityTypeDeclaration> types,
        Dictionary<string, LinkageDeclaration> linkages,
        List<string> problems)
    {
        if (Blank(identity.Name, identity.First, identity.Second))
        {
            problems.Add($"Identity '{identity.Name}' leaves its name, first or second type empty.");
            return false;
        }

        var before = problems.Count;
        types.TryGetValue(identity.First, out var first);
        types.TryGetValue(identity.Second, out var second);
        foreach (var (role, name, type) in new[] { ("first", identity.First, first), ("second", identity.Second, second) })
        {
            if (type is null)
            {
                problems.Add($"Identity '{identity.Name}' names '{name}' as its {role} type, which is not a declared entity type.");
            }
        }

        // One way of telling, not two: a field match and a linkage rule that disagree would leave no answer to which wins.
        var matches = identity.Match is not null;
        var links = identity.Linkage is not null;
        if (matches == links)
        {
            problems.Add($"Identity '{identity.Name}' must declare either a field match or a linkage rule, and not both.");
        }
        else if (links && string.IsNullOrWhiteSpace(identity.Linkage))
        {
            problems.Add($"Identity '{identity.Name}' names an empty linkage rule.");
        }
        else if (links && !linkages.TryGetValue(identity.Linkage!, out _))
        {
            problems.Add($"Identity '{identity.Name}' names the linkage rule '{identity.Linkage}', which is not declared.");
        }
        else if (links && first is not null && second is not null)
        {
            foreach (var compared in linkages[identity.Linkage!].Compare)
            {
                if (!first.Fields.ContainsKey(compared.First))
                {
                    problems.Add($"Identity '{identity.Name}' scores by '{identity.Linkage}', which compares '{compared.First}', not a field of '{first.Name}'.");
                }

                if (!second.Fields.ContainsKey(compared.Second))
                {
                    problems.Add($"Identity '{identity.Name}' scores by '{identity.Linkage}', which compares '{compared.Second}', not a field of '{second.Name}'.");
                }

                // Equality is the field match's test, so it is refused where the field match's would be.
                if (compared.Method == LinkageMethod.Exact && Unmatchable([(first, compared.First), (second, compared.Second)]) is { } why)
                {
                    problems.Add($"Identity '{identity.Name}' scores by '{identity.Linkage}', which requires equal {why}.");
                }
            }
        }
        else if (matches && identity.Match!.Count == 0)
        {
            problems.Add($"Identity '{identity.Name}' declares a field match of no fields.");
        }
        else if (matches && first is not null && second is not null)
        {
            foreach (var pair in identity.Match!)
            {
                if (pair is null || Blank(pair.First, pair.Second))
                {
                    problems.Add($"Identity '{identity.Name}' matches an empty field.");
                    continue;
                }

                if (!first.Fields.ContainsKey(pair.First))
                {
                    problems.Add($"Identity '{identity.Name}' matches '{pair.First}', which is not a field of '{first.Name}'.");
                }

                if (!second.Fields.ContainsKey(pair.Second))
                {
                    problems.Add($"Identity '{identity.Name}' matches '{pair.Second}', which is not a field of '{second.Name}'.");
                }

                if (Unmatchable([(first, pair.First), (second, pair.Second)]) is { } why)
                {
                    problems.Add($"Identity '{identity.Name}' matches {why}.");
                }
            }
        }

        return problems.Count == before;
    }

    private static bool Check(LinkageDeclaration linkage, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(linkage.Name))
        {
            problems.Add("A linkage rule leaves its name empty.");
            return false;
        }

        if (linkage.Compare is not { Count: > 0 })
        {
            problems.Add($"Linkage rule '{linkage.Name}' compares no field.");
            return false;
        }

        var before = problems.Count;
        var pairs = new HashSet<(string, string)>();
        foreach (var compared in linkage.Compare)
        {
            if (compared is null || Blank(compared.First, compared.Second))
            {
                problems.Add($"Linkage rule '{linkage.Name}' compares an empty field.");
                continue;
            }

            if (!pairs.Add((compared.First, compared.Second)))
            {
                problems.Add($"Linkage rule '{linkage.Name}' compares '{compared.First}' with '{compared.Second}' more than once.");
                continue;
            }

            if (Unscorable(compared) is { } why)
            {
                problems.Add($"Linkage rule '{linkage.Name}' compares '{compared.First}' with '{compared.Second}' {why}.");
            }
        }

        if (linkage.MatchThreshold is { } match && linkage.NonMatchThreshold is { } nonMatch && !(match > nonMatch))
        {
            problems.Add($"Linkage rule '{linkage.Name}' puts its match threshold ({match}) at or below its non-match threshold ({nonMatch}).");
        }

        if (linkage.MatchThreshold is { } m && !double.IsFinite(m) || linkage.NonMatchThreshold is { } n && !double.IsFinite(n))
        {
            problems.Add($"Linkage rule '{linkage.Name}' has a threshold that is not a number.");
        }

        return problems.Count == before;
    }

    /// <summary>Why a comparison's method and thresholds cannot score, or null when they can.</summary>
    private static string? Unscorable(LinkageComparison compared)
    {
        var thresholds = compared.Thresholds ?? [];
        if (!Enum.IsDefined(compared.Method))
        {
            return "by a method that is not one of exact, jaroWinkler, tokenJaccard or mongeElkan";
        }

        if (compared.Method == LinkageMethod.Exact && thresholds.Count > 0)
        {
            return "exactly, which takes no similarity threshold";
        }

        if (compared.Method == LinkageMethod.TokenJaccard && thresholds.Count == 0)
        {
            return "by shared words with no threshold — how many words must be shared has no default";
        }

        if (thresholds.Any(t => !(t is >= 0 and <= 1)))
        {
            return "with a threshold outside 0 to 1";
        }

        for (var i = 1; i < thresholds.Count; i++)
        {
            if (!(thresholds[i] < thresholds[i - 1]))
            {
                return "with thresholds that are not highest first";
            }
        }

        return null;
    }

    private static bool Check(GroupingDeclaration grouping, Dictionary<string, EntityTypeDeclaration> types, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(grouping.Name))
        {
            problems.Add("A grouping leaves its name empty.");
            return false;
        }

        if (grouping.Members is not { Count: > 0 })
        {
            problems.Add($"Grouping '{grouping.Name}' has no member type; a group is nothing but its members.");
            return false;
        }

        var before = problems.Count;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var by = new List<(EntityTypeDeclaration Type, string Field)>();
        foreach (var member in grouping.Members)
        {
            if (member is null || Blank(member.Type, member.By))
            {
                problems.Add($"Grouping '{grouping.Name}' has a member without a type or a field.");
                continue;
            }

            if (!seen.Add(member.Type))
            {
                problems.Add($"Grouping '{grouping.Name}' lists '{member.Type}' as a member more than once.");
                continue;
            }

            if (!types.TryGetValue(member.Type, out var type))
            {
                problems.Add($"Grouping '{grouping.Name}' has '{member.Type}' as a member, which is not a declared entity type.");
            }
            else if (!type.Fields.ContainsKey(member.By))
            {
                problems.Add($"Grouping '{grouping.Name}' groups '{member.Type}' by '{member.By}', which is not one of its fields.");
            }
            else
            {
                by.Add((type, member.By));
            }
        }

        if (Unmatchable(by) is { } why)
        {
            problems.Add($"Grouping '{grouping.Name}' groups by {why}.");
        }

        return problems.Count == before;
    }

    /// <summary>
    /// Why fields cannot be compared for the same value, or null when they can. Only what their declared kinds rule
    /// out: free text is what a person wrote, never an identifier two records share; a date equals only a date.
    /// Text, number and key fields may all hold the same code, so they compare.
    /// </summary>
    private static string? Unmatchable(IReadOnlyList<(EntityTypeDeclaration Type, string Field)> fields)
    {
        var kinds = fields
            .Select(f => (Name: $"'{f.Type.Name}.{f.Field}'", Kind: f.Type.Kinds is { } k && k.TryGetValue(f.Field, out var kind) ? kind : (FieldKind?)null))
            .ToList();

        if (kinds.FirstOrDefault(k => k.Kind == FieldKind.FreeText) is { Name: { } free })
        {
            return $"{free}, which holds free text — what a person wrote, not a value two records share";
        }

        var declared = kinds.Where(k => k.Kind is not null).ToList();
        if (declared.Any(k => k.Kind == FieldKind.Date) && declared.Any(k => k.Kind != FieldKind.Date))
        {
            return $"{string.Join(" and ", declared.Select(k => k.Name))}, a date and a value that is not one";
        }

        return null;
    }

    private static string VersionOf(IReadOnlyList<Declaration> declarations) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(declarations, OntologyJson.Default.IReadOnlyListDeclaration)))[..16];

    /// <summary>
    /// Reads a lookup in the ontology's terms: the type it starts at, the relations it follows — each
    /// leaving the type the previous one led to — and the fields it reads at the end. Says nothing
    /// about whether any entity exists; that is the sources' to answer, under the person's own access.
    /// </summary>
    /// <exception cref="UnknownTermException">The lookup names something the ontology does not declare, or walks a relation or crosses an identity from the wrong type.</exception>
    public LookupPlan Plan(LookupCall call)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (!_types.TryGetValue(call.Start.Type, out var start))
        {
            throw new UnknownTermException([$"'{call.Start.Type}' is not a declared entity type."]);
        }

        var problems = new List<string>();
        if (call.Start.Where is { } where && !start.Fields.ContainsKey(where.Field))
        {
            problems.Add($"The start names '{start.Name}' by '{where.Field}', which is not a field of '{start.Name}'.");
        }

        var steps = new List<LookupStep>();
        var at = start;
        for (var i = 0; i < call.Path.Count; i++)
        {
            var name = call.Path[i];
            if (_identities.TryGetValue(name, out var identity))
            {
                if (!string.Equals(identity.First, at.Name, StringComparison.Ordinal) && !string.Equals(identity.Second, at.Name, StringComparison.Ordinal))
                {
                    problems.Add($"Path step {i + 1}: '{name}' joins '{identity.First}' and '{identity.Second}', but the walk is at '{at.Name}'.");
                    break;
                }

                var crossed = new LookupStep(identity, at.Name);
                steps.Add(crossed);
                at = _types[crossed.Reaches];
                continue;
            }

            if (_inverses.TryGetValue(name, out var walkedBack))
            {
                if (!string.Equals(walkedBack.To, at.Name, StringComparison.Ordinal))
                {
                    problems.Add($"Path step {i + 1}: '{name}' leaves '{walkedBack.To}', but the walk is at '{at.Name}'.");
                    break;
                }

                steps.Add(new LookupStep(walkedBack, backwards: true));
                at = _types[walkedBack.From];
                continue;
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

            steps.Add(new LookupStep(relation, backwards: false));
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

        return new LookupPlan(start, steps, at, call.Read);
    }

    private static bool Blank(params string?[] values) => values.Any(string.IsNullOrWhiteSpace);
}

/// <summary>A lookup read in the ontology's terms — what to query, in which system, in which order.</summary>
/// <param name="Start">The type the walk starts at.</param>
/// <param name="Steps">The relations followed, in order, each forwards or backwards.</param>
/// <param name="End">The type the walk ends at — the start type when there are no steps.</param>
/// <param name="Read">The fields read on the end type, each one of its declared fields.</param>
public sealed record LookupPlan(
    EntityTypeDeclaration Start,
    IReadOnlyList<LookupStep> Steps,
    EntityTypeDeclaration End,
    IReadOnlyList<string> Read);

/// <summary>
/// One step of a walk: a relation followed forwards — from an entity, through its field, to the one entity
/// the field names — or backwards, from an entity to every entity whose field names it; or an identity
/// crossed, from an entity to every entity of the other type the identity holds to be the same thing.
/// </summary>
public sealed record LookupStep
{
    /// <summary>A step along <paramref name="relation"/>, backwards (by its <see cref="RelationDeclaration.Inverse"/>) when <paramref name="backwards"/>.</summary>
    public LookupStep(RelationDeclaration relation, bool backwards)
    {
        ArgumentNullException.ThrowIfNull(relation);
        Relation = relation;
        Backwards = backwards;
        Name = backwards ? relation.Inverse! : relation.Name;
        Leaves = backwards ? relation.To : relation.From;
        Reaches = backwards ? relation.From : relation.To;
    }

    /// <summary>A step across <paramref name="identity"/> from <paramref name="leaves"/>, one of its two types, to the other — the same type for duplicates within one.</summary>
    public LookupStep(IdentityDeclaration identity, string leaves)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Identity = identity;
        Name = identity.Name;
        Leaves = leaves;
        Reaches = string.Equals(leaves, identity.First, StringComparison.Ordinal) ? identity.Second : identity.First;
    }

    /// <summary>The relation followed, as declared; null when the step crosses an identity.</summary>
    public RelationDeclaration? Relation { get; }

    /// <summary>Whether the relation is walked backwards.</summary>
    public bool Backwards { get; }

    /// <summary>The identity crossed, as declared; null when the step follows a relation.</summary>
    public IdentityDeclaration? Identity { get; }

    /// <summary>The name the path gives the step: the relation's, its inverse's when walked backwards, or the identity's.</summary>
    public string Name { get; }

    /// <summary>The type the step leaves.</summary>
    public string Leaves { get; }

    /// <summary>The type the step reaches.</summary>
    public string Reaches { get; }
}
