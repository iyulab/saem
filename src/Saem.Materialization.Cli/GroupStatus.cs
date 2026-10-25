namespace Saem.Materialization.Cli;

/// <summary>What a group's store holds, in counts and times — no edge and no entity is named, so nothing of a record is in it.</summary>
/// <param name="AsOf">When the latest pass began reading the sources: the time the held edges are as of; null before a pass filed one.</param>
/// <param name="Relations">Each declared relation: the edges held and where its last incremental read left off.</param>
/// <param name="Identities">Each declared identity: the things and members held, and the pairs awaiting a person.</param>
internal sealed record GroupStatus(DateTimeOffset? AsOf, IReadOnlyList<RelationStatus> Relations, IReadOnlyList<IdentityStatus> Identities)
{
    public static async Task<GroupStatus> ReadAsync(
        Ontology.Ontology ontology, FormbaseEdgeStore edges, FormbaseIdentityStore identities, CancellationToken cancellationToken)
    {
        var asOf = await edges.AsOfAsync(cancellationToken).ConfigureAwait(false);
        var marks = await edges.WatermarksAsync(cancellationToken).ConfigureAwait(false);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        await foreach (var edge in edges.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts[edge.Relation] = counts.GetValueOrDefault(edge.Relation) + 1;
        }

        var things = (await identities.ReadAsync(cancellationToken).ConfigureAwait(false)).GroupBy(t => t.Identity, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var waiting = (await identities.CandidatesAsync(ontology, cancellationToken).ConfigureAwait(false)).GroupBy(c => c.Identity, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new GroupStatus(
            asOf,
            [.. ontology.Relations.Keys.Order(StringComparer.Ordinal).Select(r => new RelationStatus(r, counts.GetValueOrDefault(r), marks.GetValueOrDefault(r)))],
            [.. ontology.Identities.Keys.Order(StringComparer.Ordinal).Select(i =>
                new IdentityStatus(i, things.GetValueOrDefault(i)?.Count ?? 0, things.GetValueOrDefault(i)?.Sum(t => t.Members.Count) ?? 0, waiting.GetValueOrDefault(i)))]);
    }
}

/// <param name="Relation">The relation, as the ontology names it.</param>
/// <param name="Edges">How many edges the store holds of it.</param>
/// <param name="Watermark">The latest change the last incremental pass read from, as the source spells it; null when it read the relation whole.</param>
internal sealed record RelationStatus(string Relation, int Edges, string? Watermark);

/// <param name="Identity">The identity, as the ontology names it.</param>
/// <param name="Things">How many things (canonical ids) the store holds.</param>
/// <param name="Members">How many entities those things hold between them.</param>
/// <param name="Awaiting">How many pairs wait for a person's judgment.</param>
internal sealed record IdentityStatus(string Identity, int Things, int Members, int Awaiting);
