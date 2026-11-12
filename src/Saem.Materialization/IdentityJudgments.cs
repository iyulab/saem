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

    /// <summary>
    /// Refuses a judgment that is not of two entities of the identity: one of each of its two types — two of its
    /// type, where it is about duplicates — each named by the IRI the group's passes mint under <paramref name="iris"/>.
    /// A judgment wins over the rule, so one of two IRIs that are no entity (a typo, another group's, the wrong type)
    /// would stand as a thing of its own; the store files what it is given, so this is asked before it is.
    /// </summary>
    /// <exception cref="IdentityStoreException">The pair is not of the identity's types under the base IRI.</exception>
    public static void Check(Ontology.Ontology ontology, EntityIris iris, PersonJudgment judgment)
    {
        ArgumentNullException.ThrowIfNull(ontology);
        ArgumentNullException.ThrowIfNull(iris);
        ArgumentNullException.ThrowIfNull(judgment);
        if (!ontology.Identities.TryGetValue(judgment.Identity, out var identity))
        {
            return;   // an undeclared identity is the store's to refuse, with its own words
        }

        bool Names(string type, string iri) => iris.KeyOf(type, iri) is not null;
        if (!(Names(identity.First, judgment.First) && Names(identity.Second, judgment.Second)
            || Names(identity.First, judgment.Second) && Names(identity.Second, judgment.First)))
        {
            var types = identity.First == identity.Second ? $"two '{identity.First}' entities" : $"one '{identity.First}' and one '{identity.Second}' entity";
            throw new IdentityStoreException($"A judgment of '{identity.Name}' is of {types}, each named by its IRI under the group's base IRI; '{judgment.First}' and '{judgment.Second}' are not.");
        }
    }

    /// <summary>The two IRIs of a pair in ordinal order — the order a pair is filed under, whichever way it was judged.</summary>
    public static (string, string) Pair(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
}
