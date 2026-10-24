using Saem.Contracts;
using Saem.Ontology;

namespace Saem.Materialization;

/// <summary>A person's judgment of one pair an identity is about: one thing, or two.</summary>
/// <param name="Identity">The identity, as the ontology names it.</param>
/// <param name="First">One entity's IRI.</param>
/// <param name="Second">The other's.</param>
/// <param name="Same">Whether the person said the two are one thing.</param>
/// <param name="JudgedBy">An opaque id of the person — never a name.</param>
/// <param name="At">When the person judged.</param>
public sealed record PersonJudgment(string Identity, string First, string Second, bool Same, string JudgedBy, DateTimeOffset At);

/// <summary>
/// What an identity says is one thing once people's judgments are taken over the rule's (saem D-81 ②: a person's
/// judgment wins over the rule run again). For an identity the rule settles, the rule's clusters stand except where a
/// person parted a pair: both leave the cluster, since nothing says which of the two the rule was right about, and
/// stay only where a person put them. A pair a person joined is one thing whatever the rule says, and joins the things
/// either is in. For an identity a person settles, only what people joined is one thing — the rule's pairs are
/// candidates for them. A pair's latest judgment is the one that counts.
/// </summary>
public static class IdentityJudgments
{
    /// <summary>The clusters <paramref name="identity"/> stands for, from the rule's <paramref name="clusters"/> and people's <paramref name="judgments"/>.</summary>
    public static IReadOnlyList<IdentityCluster> Apply(
        IdentityDeclaration identity, IReadOnlyList<IdentityCluster> clusters, IReadOnlyList<PersonJudgment> judgments)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(clusters);
        ArgumentNullException.ThrowIfNull(judgments);

        var latest = judgments
            .Where(j => j.Identity == identity.Name && j.First != j.Second)
            .GroupBy(j => Pair(j.First, j.Second))
            .Select(g => g.OrderBy(j => j.At).Last())
            .ToList();
        var parted = latest.Where(j => !j.Same).SelectMany(j => new[] { (j.First, j.Second), (j.Second, j.First) }).ToHashSet();

        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        string Find(string x)
        {
            if (!parent.TryGetValue(x, out var p))
            {
                parent[x] = x;
                return x;
            }

            return p == x ? x : parent[x] = Find(p);
        }

        void Join(string a, string b)
        {
            var (ra, rb) = (Find(a), Find(b));
            if (ra != rb)
            {
                // The smaller IRI stands for the set, so the result does not depend on the order things were joined in.
                if (string.CompareOrdinal(ra, rb) < 0)
                {
                    parent[rb] = ra;
                }
                else
                {
                    parent[ra] = rb;
                }
            }
        }

        if (identity.Decision == IdentityDecision.Automatic)
        {
            foreach (var cluster in clusters.Where(c => c.Identity == identity.Name))
            {
                var kept = cluster.Members.Where(m => !cluster.Members.Any(o => parted.Contains((m, o)))).ToList();
                foreach (var member in kept)
                {
                    Join(kept[0], member);
                }
            }
        }

        foreach (var joined in latest.Where(j => j.Same))
        {
            Join(joined.First, joined.Second);
        }

        return [.. parent.Keys
            .GroupBy(Find, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => new IdentityCluster(identity.Name, [.. g.Order(StringComparer.Ordinal)]))
            .OrderBy(c => c.Members[0], StringComparer.Ordinal)];
    }

    /// <summary>The two IRIs of a pair in ordinal order — the order a pair is filed under, whichever way it was judged.</summary>
    public static (string, string) Pair(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
}
