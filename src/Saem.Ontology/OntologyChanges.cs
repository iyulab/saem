using Saem.Contracts;

namespace Saem.Ontology;

/// <summary>Whether a change to the declarations can break what was built on them.</summary>
public enum ChangeKind
{
    /// <summary>Adds vocabulary: every call that worked before works the same, and gives the same answer.</summary>
    Additive,

    /// <summary>
    /// Takes vocabulary away or changes what it means: a call that worked before may now be refused as unknown, or
    /// answer from another table, column or key — an agent that learned the old vocabulary is wrong without being told.
    /// </summary>
    Breaking,
}

/// <summary>One change between two sets of declarations.</summary>
/// <param name="Kind">Whether it can break what was built on the earlier set.</param>
/// <param name="Declaration">The declaration it is in, as <c>kind name</c> — <c>entityType WorkOrder</c>, <c>relation orders_item</c>.</param>
/// <param name="Change">What changed, in a sentence.</param>
public sealed record OntologyChange(ChangeKind Kind, string Declaration, string Change);

/// <summary>
/// Sorts the difference between two sets of declarations into changes that add vocabulary and changes that can break
/// what was built on the earlier set (saem SD-12). Only what the declarations say is compared, mechanically; a
/// declaration renamed reads as one removed and one added, since nothing says the two are one. Which changes a
/// deployment lets through is its own rule — this says which they are.
/// </summary>
public static class OntologyChanges
{
    /// <summary>The changes from <paramref name="before"/> to <paramref name="after"/>: breaking ones first, each kind in declaration order.</summary>
    public static IReadOnlyList<OntologyChange> Between(Ontology before, Ontology after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var changes = new List<OntologyChange>();
        Compare(changes, "entityType", before.EntityTypes, after.EntityTypes, EntityType);
        Compare(changes, "relation", before.Relations, after.Relations, Relation);
        Compare(changes, "identity", before.Identities, after.Identities, Identity);
        Compare(changes, "linkage", before.Linkages, after.Linkages, Linkage);
        Compare(changes, "grouping", before.Groupings, after.Groupings, Grouping);
        return [.. changes.OrderByDescending(c => c.Kind)];
    }

    private static void Compare<T>(
        List<OntologyChange> changes,
        string kind,
        IReadOnlyDictionary<string, T> before,
        IReadOnlyDictionary<string, T> after,
        Action<Action<ChangeKind, string>, T, T> compare)
    {
        foreach (var name in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            var declaration = $"{kind} {name}";
            void Add(ChangeKind change, string what) => changes.Add(new OntologyChange(change, declaration, what));
            switch (before.TryGetValue(name, out var was), after.TryGetValue(name, out var now))
            {
                case (true, false):
                    Add(ChangeKind.Breaking, "removed");
                    break;
                case (false, true):
                    Add(ChangeKind.Additive, "added");
                    break;
                default:
                    compare(Add, was!, now!);
                    break;
            }
        }
    }

    private static void EntityType(Action<ChangeKind, string> add, EntityTypeDeclaration was, EntityTypeDeclaration now)
    {
        if (was.System != now.System || was.Table != now.Table)
        {
            add(ChangeKind.Breaking, $"stands on {now.System}.{now.Table} instead of {was.System}.{was.Table}");
        }

        if (was.Key != now.Key)
        {
            add(ChangeKind.Breaking, $"is keyed by {now.Key} instead of {was.Key}");
        }

        if (was.KeyKind.GetValueOrDefault() != now.KeyKind.GetValueOrDefault())
        {
            add(ChangeKind.Breaking, $"compares its keys as {Kind(now.KeyKind.GetValueOrDefault())} instead of {Kind(was.KeyKind.GetValueOrDefault())}");
        }

        // Which rows a pass reads is not what a lookup answers: the hint only narrows the reading.
        if (was.ChangedAt != now.ChangedAt)
        {
            add(ChangeKind.Additive, now.ChangedAt is null ? "is read whole by every pass" : $"tells its changed rows by {now.ChangedAt}");
        }

        if (!(was.Record ?? []).SequenceEqual(now.Record ?? []))
        {
            add(ChangeKind.Breaking, $"cites its records by {Record(now)} instead of {Record(was)}");
        }

        foreach (var field in was.Fields.Keys.Union(now.Fields.Keys).Order(StringComparer.Ordinal))
        {
            switch (was.Fields.TryGetValue(field, out var column), now.Fields.TryGetValue(field, out var moved))
            {
                case (true, false):
                    add(ChangeKind.Breaking, $"field {field} removed");
                    continue;
                case (false, true):
                    add(ChangeKind.Additive, $"field {field} added");
                    continue;
                case (true, true) when column != moved:
                    add(ChangeKind.Breaking, $"field {field} reads column {moved} instead of {column}");
                    break;
            }

            var kindWas = KindOf(was, field);
            var kindNow = KindOf(now, field);
            if (kindWas is not null && kindWas != kindNow)
            {
                // A claim carries its field's kind, so a consumer that holds free text apart can lose the mark.
                add(ChangeKind.Breaking, kindNow is null ? $"field {field} no longer declares its kind" : $"field {field} is {kindNow} instead of {kindWas}");
            }
            else if (kindWas is null && kindNow is not null && was.Fields.ContainsKey(field))
            {
                add(ChangeKind.Additive, $"field {field} declares its kind, {kindNow}");
            }
        }
    }

    private static void Relation(Action<ChangeKind, string> add, RelationDeclaration was, RelationDeclaration now)
    {
        if (was.From != now.From || was.Via != now.Via || was.To != now.To)
        {
            add(ChangeKind.Breaking, $"leads from {now.From}.{now.Via} to {now.To} instead of from {was.From}.{was.Via} to {was.To}");
        }

        switch (was.Inverse, now.Inverse)
        {
            case (null, { } added):
                add(ChangeKind.Additive, $"may be walked backwards as {added}");
                break;
            case ({ } removed, null):
                add(ChangeKind.Breaking, $"may no longer be walked backwards as {removed}");
                break;
            case ({ } old, { } renamed) when old != renamed:
                add(ChangeKind.Breaking, $"is walked backwards as {renamed} instead of {old}");
                break;
        }
    }

    private static void Identity(Action<ChangeKind, string> add, IdentityDeclaration was, IdentityDeclaration now)
    {
        // What an identity finds is recomputed whole from its declaration, so any change can join or part things.
        if (was.First != now.First || was.Second != now.Second || was.Decision != now.Decision || was.Linkage != now.Linkage
            || !(was.Match ?? []).SequenceEqual(now.Match ?? []))
        {
            add(ChangeKind.Breaking, "tells what is one thing otherwise");
        }
    }

    private static void Linkage(Action<ChangeKind, string> add, LinkageDeclaration was, LinkageDeclaration now)
    {
        // Every identity that names the rule is scored by it, so any change can join or part what they find.
        if (!was.Compare.SequenceEqual(now.Compare, LinkageComparisons.Instance)
            || was.MatchThreshold != now.MatchThreshold || was.NonMatchThreshold != now.NonMatchThreshold)
        {
            add(ChangeKind.Breaking, "scores what is one thing otherwise");
        }
    }

    /// <summary>Two comparisons are the same when they compare the same fields the same way — thresholds by value.</summary>
    private sealed class LinkageComparisons : IEqualityComparer<LinkageComparison>
    {
        public static readonly LinkageComparisons Instance = new();

        public bool Equals(LinkageComparison? x, LinkageComparison? y) =>
            x is not null && y is not null && x.First == y.First && x.Second == y.Second && x.Method == y.Method
            && (x.Thresholds ?? []).SequenceEqual(y.Thresholds ?? []);

        public int GetHashCode(LinkageComparison obj) => HashCode.Combine(obj.First, obj.Second, obj.Method);
    }

    private static void Grouping(Action<ChangeKind, string> add, GroupingDeclaration was, GroupingDeclaration now)
    {
        var before = was.Members.ToDictionary(m => m.Type, m => m.By, StringComparer.Ordinal);
        var after = now.Members.ToDictionary(m => m.Type, m => m.By, StringComparer.Ordinal);
        foreach (var type in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            switch (before.TryGetValue(type, out var by), after.TryGetValue(type, out var regrouped))
            {
                case (true, false):
                    add(ChangeKind.Breaking, $"no longer has {type} as a member");
                    break;
                case (false, true):
                    add(ChangeKind.Additive, $"has {type} as a member, by {regrouped}");
                    break;
                case (true, true) when by != regrouped:
                    add(ChangeKind.Breaking, $"groups {type} by {regrouped} instead of {by}");
                    break;
            }
        }
    }

    private static FieldKind? KindOf(EntityTypeDeclaration type, string field) =>
        type.Kinds is { } kinds && kinds.TryGetValue(field, out var kind) ? kind : null;

    private static string Kind(KeyKind kind) => kind == KeyKind.Number ? "numbers" : "text";

    private static string Record(EntityTypeDeclaration type) => type.Record is { } columns ? string.Join('/', columns) : "its key";
}
