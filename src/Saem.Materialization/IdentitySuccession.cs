namespace Saem.Materialization;

/// <summary>
/// One thing an identity found, under the canonical id it keeps from pass to pass: the id, when it was first issued,
/// and the entities it is now.
/// </summary>
/// <param name="Identity">The identity, as the ontology names it.</param>
/// <param name="Id">The canonical id — opaque, issued once, never derived from a member, so it does not change with the order rows were read in.</param>
/// <param name="Issued">When the id was first issued; the older of two ids is the one that survives their merge.</param>
/// <param name="Members">The members' IRIs, in ordinal order.</param>
public sealed record CanonicalIdentity(string Identity, string Id, DateTimeOffset Issued, IReadOnlyList<string> Members)
{
    /// <summary>Equal when every part is, the members compared one by one in order.</summary>
    public bool Equals(CanonicalIdentity? other) =>
        other is not null && Identity == other.Identity && Id == other.Id && Issued == other.Issued && Members.SequenceEqual(other.Members);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Identity, Id, Issued, Members.Count);
}

/// <summary>What a pass makes of the canonical ids an identity held before it.</summary>
/// <param name="Standing">The canonical ids the identity holds now, one per cluster the pass found, in order of their first member.</param>
/// <param name="Retired">The ids held before that no cluster carries on — merged into an older one, or with no members left.</param>
public sealed record IdentitySuccessionResult(IReadOnlyList<CanonicalIdentity> Standing, IReadOnlyList<string> Retired);

/// <summary>
/// Carries canonical ids from one pass to the next (saem D-81 ②), so that a thing keeps its id while what is known of
/// it changes. Each id held before goes to the cluster that holds most of its members — the fewest members moved; on a
/// tie, the cluster whose first member comes first. A cluster that receives ids keeps the oldest, and the others are
/// retired: <b>a merge keeps the older id</b>. A cluster that receives none is issued a new one: <b>the part split off
/// gets a new id</b>, and the part that kept most of the members keeps the old one. A thing found for the first time is
/// issued an id once, and keeps it as long as some cluster carries it on.
/// </summary>
public static class IdentitySuccession
{
    /// <summary>The canonical ids <paramref name="clusters"/> carry on from <paramref name="held"/>.</summary>
    /// <param name="held">The canonical ids the identity held before the pass.</param>
    /// <param name="clusters">The clusters the pass found, all of one identity.</param>
    /// <param name="now">When the pass issues an id it has to.</param>
    /// <param name="issue">Issues a new canonical id, distinct from every id issued before.</param>
    public static IdentitySuccessionResult Succeed(
        IReadOnlyList<CanonicalIdentity> held,
        IReadOnlyList<IdentityCluster> clusters,
        DateTimeOffset now,
        Func<string> issue)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(clusters);
        ArgumentNullException.ThrowIfNull(issue);

        var ordered = clusters.OrderBy(c => c.Members[0], StringComparer.Ordinal).ToList();
        if (ordered.Select(c => c.Identity).Concat(held.Select(h => h.Identity)).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            throw new ArgumentException("The held ids and the clusters must all be of one identity.", nameof(clusters));
        }

        var clusterOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < ordered.Count; i++)
        {
            foreach (var member in ordered[i].Members)
            {
                if (!clusterOf.TryAdd(member, i))
                {
                    throw new ArgumentException($"'{member}' is in two clusters; the clusters of one identity never share an entity.", nameof(clusters));
                }
            }
        }

        // Each held id goes to the cluster that holds most of its members — the first such cluster on a tie.
        var received = ordered.Select(_ => new List<CanonicalIdentity>()).ToList();
        var retired = new List<string>();
        foreach (var id in held)
        {
            var heir = id.Members
                .Where(clusterOf.ContainsKey)
                .GroupBy(m => clusterOf[m])
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .Select(g => (int?)g.Key)
                .FirstOrDefault();
            if (heir is { } cluster)
            {
                received[cluster].Add(id);
            }
            else
            {
                retired.Add(id.Id);
            }
        }

        var standing = new List<CanonicalIdentity>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var ids = received[i].OrderBy(h => h.Issued).ThenBy(h => h.Id, StringComparer.Ordinal).ToList();
            var kept = ids.FirstOrDefault();
            retired.AddRange(ids.Skip(1).Select(h => h.Id));
            standing.Add(new CanonicalIdentity(ordered[i].Identity, kept?.Id ?? issue(), kept?.Issued ?? now, ordered[i].Members));
        }

        return new IdentitySuccessionResult(standing, [.. retired.Order(StringComparer.Ordinal)]);
    }
}
