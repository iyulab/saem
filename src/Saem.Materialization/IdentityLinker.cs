using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Eyu.Core.Linkage;
using Eyu.Core.Records;
using Saem.Contracts;
using Saem.Ontology;
using Saem.Query;

namespace Saem.Materialization;

/// <summary>
/// What an identity by a linkage rule was fitted to: how often each compared field agrees on pairs that are one thing
/// and on pairs that are two (Fellegi–Sunter m and u), fitted once over every entity the group's account reads.
/// Kept in the group's store and handed to every pass, so a pass scores the same pair the same way however few rows
/// changed — fitted again each pass, the scores would drift with what happens to be read and the canonical ids with
/// them (saem D-87 ⓑ). The parameters are counts over the group's records, never a value any record holds.
/// </summary>
/// <param name="Identity">The identity, as the ontology names it.</param>
/// <param name="Rule">
/// Which declarations the fit stands for — a hash of the identity and its linkage rule. A pass scores only with a fit
/// whose rule is the one declared now: the parameters are per compared field, so a rule changed since is a fit of
/// something else.
/// </param>
/// <param name="Parameters">The fitted parameters.</param>
/// <param name="Records">How many entities the fit read, of both types.</param>
/// <param name="FittedAt">When the fit ran.</param>
public sealed record LinkageFit(string Identity, string Rule, FieldLinkageParameters Parameters, int Records, DateTimeOffset FittedAt);

/// <summary>Why an identity by a linkage rule was left out of a pass.</summary>
/// <param name="Identity">The identity, as the ontology names it.</param>
/// <param name="Reason">Why, in a sentence that names no entity.</param>
public sealed record LinkageSkip(string Identity, string Reason);

/// <summary>
/// Scores the identities that name a linkage rule (saem D-87): reads both types whole with the group's account, the
/// compared columns only, and asks Eyu's Fellegi–Sunter linkage which pairs are one thing — never two rows of one type
/// when the identity joins two, since a type's rows are told apart by their keys. Fitting (<see cref="FitAsync"/>) and
/// scoring (<see cref="ScoreAsync"/>) are separate: a pass scores with the parameters a fit kept, and leaves out an
/// identity with none.
/// <para>
/// What a pass gives depends on who settles the identity. One the rule settles (<c>automatic</c>) is the clusters the
/// pairs scored a match join, each with those pairs as its grounds; a pair between the thresholds joins nothing. One a
/// person settles (<c>confirmed</c>) is every pair scored a match or undecided, each a candidate of its own for that
/// person — no pair is one thing until a person says so (<see cref="IdentityJudgments"/>).
/// </para>
/// <para>
/// Every pair of the two types is compared, so the cost grows with the product of their sizes; a type of many
/// thousands of rows wants blocking first, which this does not yet do.
/// </para>
/// </summary>
public sealed class IdentityLinker(Ontology.Ontology ontology, EntityIris iris)
{
    /// <summary>Fits <paramref name="identity"/>'s rule to what the sessions read.</summary>
    /// <exception cref="ArgumentException">The identity is not declared, or names no linkage rule.</exception>
    /// <exception cref="InvalidOperationException">No pair could be compared: fewer than two entities, or none of each type.</exception>
    public async Task<LinkageFit> FitAsync(ISourceSessions sessions, string identity, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var (declared, rule) = Linked(identity);
        var records = await ReadAsync(sessions, declared, rule, cancellationToken).ConfigureAwait(false);
        var analysis = LinkagePipeline.Analyze(records.All, [], Options(rule), cannotLink: records.CannotLink);
        return analysis.Parameters is { } fitted
            ? new LinkageFit(declared.Name, RuleOf(declared, rule), fitted, records.All.Count, now)
            : throw new InvalidOperationException($"'{declared.Name}' has no pair to fit on: it needs entities on both sides of the identity.");
    }

    /// <summary>
    /// The clusters (or, for an identity a person settles, the candidate pairs) of every identity by a linkage rule,
    /// identity by identity in name order, scored with <paramref name="fits"/>. An identity whose fit is missing or was
    /// made for another rule gives nothing and is told to <paramref name="skipped"/>.
    /// </summary>
    public async IAsyncEnumerable<IdentityCluster> ScoreAsync(
        ISourceSessions sessions,
        IReadOnlyDictionary<string, LinkageFit> fits,
        Action<LinkageSkip>? skipped = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(fits);

        foreach (var identity in ontology.Identities.Values.Where(i => i.Linkage is not null).OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            var rule = ontology.Linkages[identity.Linkage!];
            if (!fits.TryGetValue(identity.Name, out var fit))
            {
                skipped?.Invoke(new LinkageSkip(identity.Name, "no fit is kept for it — run the fit first"));
                continue;
            }

            if (fit.Rule != RuleOf(identity, rule))
            {
                skipped?.Invoke(new LinkageSkip(identity.Name, "its fit was made for a rule declared otherwise — fit it again"));
                continue;
            }

            var records = await ReadAsync(sessions, identity, rule, cancellationToken).ConfigureAwait(false);
            var analysis = LinkagePipeline.Analyze(records.All, [], fit.Parameters, Options(rule), cannotLink: records.CannotLink);
            foreach (var cluster in Clusters(identity, rule, analysis.PairLinkages))
            {
                yield return cluster;
            }
        }
    }

    /// <summary>
    /// Which declarations a fit stands for: a hash of the identity's types and its rule, as declared. Who settles the
    /// identity is left out — the parameters do not depend on it.
    /// </summary>
    public static string RuleOf(IdentityDeclaration identity, LinkageDeclaration rule)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(rule);
        var stated = JsonSerializer.SerializeToUtf8Bytes<IReadOnlyList<Declaration>>(
            [identity with { Decision = IdentityDecision.Automatic }, rule], OntologyJson.Default.IReadOnlyListDeclaration);
        return Convert.ToHexStringLower(SHA256.HashData(stated))[..16];
    }

    private static IEnumerable<IdentityCluster> Clusters(IdentityDeclaration identity, LinkageDeclaration rule, IReadOnlyList<PairLinkage> pairs)
    {
        static IdentityGround Ground(LinkageDeclaration rule, PairLinkage pair)
        {
            var (first, second) = IdentityJudgments.Pair(pair.RecordIdA, pair.RecordIdB);
            return new IdentityGround(first, second, rule.Name, pair.LogLikelihoodRatio);
        }

        if (identity.Decision == IdentityDecision.Confirmed)
        {
            return pairs
                .Where(p => p.Classification != LinkageClassification.NonMatch)
                .Select(p => Ground(rule, p))
                .Select(g => new IdentityCluster(identity.Name, [g.First, g.Second]) { Grounds = [g] })
                .OrderBy(c => c.Members[0], StringComparer.Ordinal)
                .ThenBy(c => c.Members[1], StringComparer.Ordinal);
        }

        // The pairs a rule scored a match are what it states; the things are what those pairs join, and nothing more.
        var matched = pairs.Where(p => p.Classification == LinkageClassification.Match).Select(p => Ground(rule, p)).ToList();
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        string Find(string x) => !parent.TryGetValue(x, out var p) ? parent[x] = x : p == x ? x : parent[x] = Find(p);
        foreach (var ground in matched)
        {
            var (a, b) = (Find(ground.First), Find(ground.Second));
            if (a != b)
            {
                parent[string.CompareOrdinal(a, b) < 0 ? b : a] = string.CompareOrdinal(a, b) < 0 ? a : b;
            }
        }

        return matched
            .GroupBy(g => Find(g.First), StringComparer.Ordinal)
            .Select(g => new IdentityCluster(
                identity.Name,
                [.. g.SelectMany(p => new[] { p.First, p.Second }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)])
            {
                Grounds = [.. g.OrderBy(p => p.First, StringComparer.Ordinal).ThenBy(p => p.Second, StringComparer.Ordinal)],
            })
            .OrderBy(c => c.Members[0], StringComparer.Ordinal);
    }

    private (IdentityDeclaration Identity, LinkageDeclaration Rule) Linked(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!ontology.Identities.TryGetValue(name, out var identity))
        {
            throw new ArgumentException($"'{name}' is not a declared identity.", nameof(name));
        }

        return identity.Linkage is { } linkage
            ? (identity, ontology.Linkages[linkage])
            : throw new ArgumentException($"'{name}' tells what is one thing by a field match, not a linkage rule; it has nothing to fit.", nameof(name));
    }

    /// <summary>
    /// The entities of both types as Eyu records: each named by its IRI, its fields the rule's comparisons — the first
    /// type's value of a pair's first field and the second type's of its second, under one name per pair, so the two
    /// are compared. Within one type, a pair's first field is read.
    /// </summary>
    private async Task<(List<RawRecord> All, Func<RawRecord, RawRecord, bool> CannotLink)> ReadAsync(
        ISourceSessions sessions, IdentityDeclaration identity, LinkageDeclaration rule, CancellationToken cancellationToken)
    {
        var first = ontology.EntityTypes[identity.First];
        var second = ontology.EntityTypes[identity.Second];
        var within = ReferenceEquals(first, second);
        var sides = new List<(EntityTypeDeclaration Type, Func<LinkageComparison, string> Field)> { (first, c => c.First) };
        if (!within)
        {
            sides.Add((second, c => c.Second));
        }

        var records = new List<RawRecord>();
        var sideOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var opened = new Dictionary<string, ISourceSession>(StringComparer.Ordinal);
        try
        {
            for (var side = 0; side < sides.Count; side++)
            {
                var (type, field) = sides[side];
                if (!opened.TryGetValue(type.System, out var session))
                {
                    session = await sessions.OpenAsync(type.System, cancellationToken).ConfigureAwait(false);
                    opened.Add(type.System, session);
                }

                var columns = rule.Compare.Select(c => type.Fields[field(c)]).Distinct(StringComparer.Ordinal).ToList();
                await foreach (var row in session.ScanAsync(type.Table, type.Key, columns, cancellationToken).ConfigureAwait(false))
                {
                    var iri = iris.For(type.Name, type.Normalize(row.Key));
                    if (!sideOf.TryAdd(iri, side))
                    {
                        continue;
                    }

                    records.Add(new RawRecord(iri, rule.Compare.ToDictionary(Label, c => Text(row.Values[type.Fields[field(c)]]), StringComparer.Ordinal)));
                }
            }
        }
        finally
        {
            foreach (var session in opened.Values)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        // Two rows of one type are two entities — their keys say so — when the identity joins two types.
        bool CannotLink(RawRecord a, RawRecord b) => !within && sideOf[a.Id] == sideOf[b.Id];
        return (records, CannotLink);
    }

    private static LinkageOptions Options(LinkageDeclaration rule) => new(
        MatchThreshold: rule.MatchThreshold ?? LinkageClassifier.DefaultMatchThreshold,
        NonMatchThreshold: rule.NonMatchThreshold ?? LinkageClassifier.DefaultNonMatchThreshold,
        FieldComparisons: rule.Compare.ToDictionary(Label, Comparison, StringComparer.Ordinal));

    private static string Label(LinkageComparison compared) => compared.First + "=" + compared.Second;

    private static FieldComparison Comparison(LinkageComparison compared)
    {
        double[] thresholds = [.. compared.Thresholds ?? []];
        return compared.Method switch
        {
            LinkageMethod.JaroWinkler => FieldComparison.JaroWinkler(thresholds),
            LinkageMethod.TokenJaccard => FieldComparison.TokenJaccard(thresholds[0], thresholds[1..]),
            LinkageMethod.MongeElkan => FieldComparison.MongeElkan(thresholds),
            _ => FieldComparison.Exact,
        };
    }

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        _ => value.GetRawText(),
    };
}
