using System.Globalization;

namespace Saem.Materialization;

/// <summary>
/// Keeps what a group's identities found in that group's own Formbase, beside its edges (saem D-81 ① ③), under
/// three form types of text fields — none a record's body could be written into:
/// <list type="bullet">
/// <item><see cref="CanonicalFormType"/> — one record per canonical id: the identity, the id and when it was issued.</item>
/// <item><see cref="MemberFormType"/> — the cross-references: one record per entity in a thing, naming its canonical id.</item>
/// <item><see cref="GroundFormType"/> — the grounds each was judged on: the rule, its score, the pair of entities, who
/// judged and in which pass. A ground names entities by their IRIs and holds no value any record holds.</item>
/// </list>
/// <para>
/// A pass carries the canonical ids on (<see cref="IdentitySuccession"/>), then makes each form type hold exactly what
/// it gives, as the edge store does: a record held unchanged is not sent again, a changed one is sent as a correction,
/// one no longer given is retired.
/// </para>
/// <para>
/// A person's judgment of a pair (<see cref="JudgeAsync"/>) is a ground of its own, judged by that person, and wins over
/// the rule run again (<see cref="IdentityJudgments"/>): a pass keeps it, and makes things of the rule's clusters and
/// people's judgments together. An identity whose pairs a person settles (<c>confirmed</c>) has the rule's pairs as
/// candidates only — its grounds are filed, and a canonical id is issued once a person has said a pair is one.
/// </para>
/// </summary>
/// <param name="http">A client whose <see cref="HttpClient.BaseAddress"/> is the group's Formbase. The caller owns its lifetime.</param>
/// <param name="formbaseNamespace">The Formbase namespace to address, sent as <c>Formbase-Namespace</c>; omitted, the instance's own.</param>
/// <param name="time">The clock a pass's ids are issued and its grounds judged by; omitted, the system's.</param>
/// <param name="issue">Issues a new canonical id; omitted, a random one.</param>
public sealed class FormbaseIdentityStore(HttpClient http, string? formbaseNamespace = null, TimeProvider? time = null, Func<string>? issue = null)
{
    /// <summary>The form type the canonical ids are filed under.</summary>
    public const string CanonicalFormType = "identities";

    /// <summary>The form type the cross-references are filed under.</summary>
    public const string MemberFormType = "identity_members";

    /// <summary>The form type the grounds are filed under.</summary>
    public const string GroundFormType = "identity_grounds";

    /// <summary>The rule a field match is judged by: equal values, scored 1.</summary>
    public const string MatchRule = "match";

    /// <summary>Who judged a ground when no person did.</summary>
    public const string RuleJudge = "rule";

    /// <summary>The rule a person's judgment is filed under: scored 1 when the person said the pair is one thing, 0 when two.</summary>
    public const string PersonRule = "person";

    private static readonly string[] CanonicalFields = ["identity", "canonical_id", "issued"];

    private static readonly string[] MemberFields = ["identity", "entity_iri", "canonical_id"];

    private static readonly string[] GroundFields = ["identity", "first_iri", "second_iri", "rule", "score", "judged_by", "generation"];

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private readonly Func<string> _issue = issue ?? (() => Guid.NewGuid().ToString("N"));

    private readonly FormbaseForms _forms = new(http, formbaseNamespace, message => new IdentityStoreException(message));

    /// <summary>
    /// Makes the store hold what <paramref name="clusters"/> say of the identities of <paramref name="ontology"/>,
    /// with the canonical ids carried on from what it held; an identity no longer declared leaves nothing behind.
    /// </summary>
    public async Task<IdentityWrite> ReplaceAsync(
        Ontology.Ontology ontology, IAsyncEnumerable<IdentityCluster> clusters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ontology);
        ArgumentNullException.ThrowIfNull(clusters);

        await _forms.DeclareAsync(CanonicalFormType, "saem_identities", CanonicalFields, cancellationToken).ConfigureAwait(false);
        await _forms.DeclareAsync(MemberFormType, "saem_identity_members", MemberFields, cancellationToken).ConfigureAwait(false);
        await _forms.DeclareAsync(GroundFormType, "saem_identity_grounds", GroundFields, cancellationToken).ConfigureAwait(false);
        var heldCanonical = await _forms.HeldAsync(CanonicalFormType, "", CanonicalFields, cancellationToken).ConfigureAwait(false);
        var heldMembers = await _forms.HeldAsync(MemberFormType, "", MemberFields, cancellationToken).ConfigureAwait(false);
        var heldGrounds = await _forms.HeldAsync(GroundFormType, "", GroundFields, cancellationToken).ConfigureAwait(false);

        var found = new Dictionary<string, List<IdentityCluster>>(StringComparer.Ordinal);
        await foreach (var cluster in clusters.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (!ontology.Identities.ContainsKey(cluster.Identity))
            {
                throw new IdentityStoreException($"A cluster of '{cluster.Identity}' was given, which is not a declared identity.");
            }

            if (!found.TryGetValue(cluster.Identity, out var list))
            {
                found.Add(cluster.Identity, list = []);
            }

            list.Add(cluster);
        }

        var now = _time.GetUtcNow();
        var generation = now.ToString("O", CultureInfo.InvariantCulture);
        var canonical = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var members = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var grounds = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var judgments = Judgments(ontology, heldGrounds);
        int issued = 0, merged = 0;
        foreach (var identity in ontology.Identities.Values.OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            var given = found.GetValueOrDefault(identity.Name) ?? [];
            foreach (var (key, ground) in heldGrounds.Where(g => g.Value[0] == identity.Name && g.Value[3] == PersonRule))
            {
                grounds[key] = ground;
            }

            foreach (var cluster in given)
            {
                // A ground per member after the first, each pairing it with the first: the cluster is one by equal values,
                // so every pair is, and these name each member once.
                foreach (var other in cluster.Members.Skip(1))
                {
                    var key = $"{identity.Name} {cluster.Members[0]} {other}";
                    string[] ground = [identity.Name, cluster.Members[0], other, MatchRule, "1", RuleJudge, generation];

                    // A ground judged the same way before keeps the pass that first judged it.
                    grounds[key] = heldGrounds.TryGetValue(key, out var before) && before.AsSpan(0, 6).SequenceEqual(ground.AsSpan(0, 6)) ? before : ground;
                }
            }

            var held = HeldOf(identity.Name, heldCanonical, heldMembers);
            var things = IdentityJudgments.Apply(identity, given, judgments);
            var succession = IdentitySuccession.Succeed(held, things, now, _issue);
            var heldIds = held.ToDictionary(h => h.Id, StringComparer.Ordinal);
            var standingMembers = succession.Standing.SelectMany(t => t.Members).ToHashSet(StringComparer.Ordinal);
            issued += succession.Standing.Count(t => !heldIds.ContainsKey(t.Id));
            merged += succession.Retired.Count(id => heldIds[id].Members.Any(standingMembers.Contains));
            foreach (var thing in succession.Standing)
            {
                canonical[thing.Id] = [identity.Name, thing.Id, thing.Issued.ToString("O", CultureInfo.InvariantCulture)];
                foreach (var member in thing.Members)
                {
                    members[$"{identity.Name} {member}"] = [identity.Name, member, thing.Id];
                }
            }
        }

        return new IdentityWrite(
            await MakeHoldAsync(CanonicalFormType, CanonicalFields, heldCanonical, canonical, "canonical id", cancellationToken).ConfigureAwait(false),
            await MakeHoldAsync(MemberFormType, MemberFields, heldMembers, members, "cross-reference", cancellationToken).ConfigureAwait(false),
            await MakeHoldAsync(GroundFormType, GroundFields, heldGrounds, grounds, "ground", cancellationToken).ConfigureAwait(false))
        {
            Issued = issued,
            Merged = merged,
        };
    }

    /// <summary>
    /// Compares what the store holds with <paramref name="clusters"/> — what a pass would find now — and writes nothing
    /// (saem D-81 ⑤). Canonical ids are issued, not derived, so a thing is compared by its members: held as found, found
    /// and not held, held with other members, or held and no longer found; grounds by the pair and how it was judged.
    /// Counts only — no entity is named.
    /// </summary>
    public async Task<IdentityComparison> CompareAsync(
        Ontology.Ontology ontology, IAsyncEnumerable<IdentityCluster> clusters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ontology);
        ArgumentNullException.ThrowIfNull(clusters);

        var skipped = 0;
        var heldCanonical = await _forms.HeldAsync(CanonicalFormType, "", CanonicalFields, cancellationToken, readOn: n => skipped += n).ConfigureAwait(false);
        var heldMembers = await _forms.HeldAsync(MemberFormType, "", MemberFields, cancellationToken, readOn: n => skipped += n).ConfigureAwait(false);
        var heldGrounds = await _forms.HeldAsync(GroundFormType, "", GroundFields, cancellationToken, readOn: n => skipped += n).ConfigureAwait(false);

        var found = new List<IdentityCluster>();
        await foreach (var cluster in clusters.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            found.Add(cluster);
        }

        // Things by "identity member member …", the members in order, as a pass would make them of the rule's clusters and
        // people's judgments; a held thing of an identity no longer declared is held and not given.
        static string Thing(string identity, IEnumerable<string> members) => identity + " " + string.Join(' ', members);
        var judgments = Judgments(ontology, heldGrounds);
        var things = ontology.Identities.Values
            .SelectMany(i => IdentityJudgments.Apply(i, [.. found.Where(c => c.Identity == i.Name)], judgments))
            .ToList();
        var givenThings = things.Select(c => Thing(c.Identity, c.Members)).ToHashSet(StringComparer.Ordinal);
        var heldThings = heldCanonical.Values.Select(c => c[0]).Distinct(StringComparer.Ordinal)
            .SelectMany(identity => HeldOf(identity, heldCanonical, heldMembers))
            .Select(h => (Key: Thing(h.Identity, h.Members), h.Identity, h.Members))
            .ToList();
        var heldKeys = heldThings.Select(h => h.Key).ToHashSet(StringComparer.Ordinal);
        var heldMember = heldThings.SelectMany(h => h.Members.Select(m => h.Identity + " " + m)).ToHashSet(StringComparer.Ordinal);
        var givenMember = things.SelectMany(c => c.Members.Select(m => c.Identity + " " + m)).ToHashSet(StringComparer.Ordinal);

        var matching = givenThings.Count(heldKeys.Contains);
        var unmatched = things.Where(c => !heldKeys.Contains(Thing(c.Identity, c.Members))).ToList();
        var differing = unmatched.Count(c => c.Members.Any(m => heldMember.Contains(c.Identity + " " + m)));
        var extra = heldThings.Count(h => !givenThings.Contains(h.Key) && !h.Members.Any(m => givenMember.Contains(h.Identity + " " + m)));

        var givenGrounds = found.Where(c => ontology.Identities.ContainsKey(c.Identity))
            .SelectMany(c => c.Members.Skip(1).Select(other => $"{c.Identity} {c.Members[0]} {other}"))
            .Concat(heldGrounds.Where(g => g.Value[3] == PersonRule && ontology.Identities.ContainsKey(g.Value[0])).Select(g => g.Key))
            .ToHashSet(StringComparer.Ordinal);
        var groundsMissing = givenGrounds.Count(k => !heldGrounds.ContainsKey(k));
        var groundsExtra = heldGrounds.Keys.Count(k => !givenGrounds.Contains(k));

        return new IdentityComparison(matching, unmatched.Count - differing, differing, extra, groundsMissing, groundsExtra, skipped);
    }

    /// <summary>
    /// Files a person's judgment of a pair: a ground of its own, under <see cref="PersonRule"/> and the person's opaque id,
    /// that a pass keeps and takes over the rule's. A later judgment of the same pair corrects it. The things the store
    /// holds change on the next pass.
    /// </summary>
    /// <exception cref="IdentityStoreException">The identity is not declared, the pair is one entity, or the person is not named.</exception>
    public async Task JudgeAsync(Ontology.Ontology ontology, PersonJudgment judgment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ontology);
        ArgumentNullException.ThrowIfNull(judgment);
        if (!ontology.Identities.ContainsKey(judgment.Identity))
        {
            throw new IdentityStoreException($"'{judgment.Identity}' is not a declared identity.");
        }

        if (string.IsNullOrWhiteSpace(judgment.First) || string.IsNullOrWhiteSpace(judgment.Second) || judgment.First == judgment.Second)
        {
            throw new IdentityStoreException("A judgment is of two entities, each named by its IRI.");
        }

        if (string.IsNullOrWhiteSpace(judgment.JudgedBy))
        {
            throw new IdentityStoreException("A judgment names who judged it, by an opaque id.");
        }

        var (first, second) = IdentityJudgments.Pair(judgment.First, judgment.Second);
        await _forms.DeclareAsync(GroundFormType, "saem_identity_grounds", GroundFields, cancellationToken).ConfigureAwait(false);
        await _forms.FileAsync(
            GroundFormType,
            $"{judgment.Identity} {first} {second} {PersonRule}",
            GroundFields,
            [judgment.Identity, first, second, PersonRule, judgment.Same ? "1" : "0", judgment.JudgedBy, judgment.At.ToString("O", CultureInfo.InvariantCulture)],
            "Filing a judgment",
            cancellationToken).ConfigureAwait(false);
        await _forms.ProjectAsync(GroundFormType, "Projecting the grounds", cancellationToken).ConfigureAwait(false);
    }

    private static List<PersonJudgment> Judgments(Ontology.Ontology ontology, Dictionary<string, string[]> grounds) =>
        [.. grounds.Values
            .Where(g => g[3] == PersonRule && ontology.Identities.ContainsKey(g[0]))
            .Select(g => new PersonJudgment(
                g[0], g[1], g[2], g[4] == "1", g[5],
                DateTimeOffset.TryParseExact(g[6], "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                    ? at
                    : throw new IdentityStoreException($"A judgment of '{g[0]}' names its time as '{g[6]}', which is not a round-trip time.")))];

    /// <summary>The things each identity holds now, by canonical id, each with its members — every one of them, or a failure when a projection skipped documents.</summary>
    public async Task<IReadOnlyList<CanonicalIdentity>> ReadAsync(CancellationToken cancellationToken = default)
    {
        var heldCanonical = await _forms.HeldAsync(CanonicalFormType, "", CanonicalFields, cancellationToken).ConfigureAwait(false);
        var heldMembers = await _forms.HeldAsync(MemberFormType, "", MemberFields, cancellationToken).ConfigureAwait(false);
        return [.. heldCanonical.Values.Select(c => c[0]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .SelectMany(identity => HeldOf(identity, heldCanonical, heldMembers))];
    }

    /// <summary>
    /// The IRIs of the entities <paramref name="identity"/> holds to be the same thing as the one named <paramref name="iri"/> —
    /// the members of its canonical id, never that entity itself; none when the entity is in no thing. A member is filed only once its pair is settled as one (by the rule where the identity is
    /// <c>automatic</c> and no person said otherwise, by a person where it is <c>confirmed</c>), so the members are what may
    /// be said to be the same. A projection that skipped documents is read over the records that stand and reported, as the
    /// edge store's walk is: a member missing from a skipped document can only leave one out.
    /// </summary>
    public async Task<SameAs> SameAsAsync(string identity, string iri, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(identity);
        ArgumentException.ThrowIfNullOrEmpty(iri);
        var skipped = 0;
        var of = Filter(identity, "entity_iri", iri);
        var own = await _forms.HeldAsync(MemberFormType, of, MemberFields, cancellationToken, readOn: count => skipped = count).ConfigureAwait(false);
        if (own.Values.Select(m => m[2]).Distinct(StringComparer.Ordinal).ToList() is not [var canonicalId])
        {
            return own.Count == 0
                ? new SameAs([], skipped)
                : throw new IdentityStoreException($"'{iri}' is filed under more than one canonical id of '{identity}'; a pass files each entity under one.");
        }

        // The thing is read whole: its members are of both types and the caller keeps one, so a cut by count could leave out
        // members the caller would have counted. A thing is as large as the entities one value or one link ties together.
        var members = await _forms.HeldAsync(
            MemberFormType, Filter(identity, "canonical_id", canonicalId), MemberFields, cancellationToken, readOn: count => skipped = count).ConfigureAwait(false);
        return new SameAs([.. members.Values.Select(m => m[1]).Where(m => m != iri).Order(StringComparer.Ordinal)], skipped);
    }

    private static string Filter(string identity, string field, string value) =>
        $"&filter={Uri.EscapeDataString("identity:" + identity)}&filter={Uri.EscapeDataString(field + ":" + value)}";

    private static List<CanonicalIdentity> HeldOf(string identity, Dictionary<string, string[]> canonical, Dictionary<string, string[]> members)
    {
        var byId = members.Values.Where(m => m[0] == identity).GroupBy(m => m[2], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(m => m[1]).Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        return [.. canonical.Values.Where(c => c[0] == identity).OrderBy(c => c[1], StringComparer.Ordinal).Select(c => new CanonicalIdentity(
            identity,
            c[1],
            DateTimeOffset.TryParseExact(c[2], "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                ? at
                : throw new IdentityStoreException($"The canonical id '{c[1]}' names its issue as '{c[2]}', which is not a round-trip time."),
            byId.GetValueOrDefault(c[1]) ?? []))];
    }

    private async Task<RecordWrite> MakeHoldAsync(
        string formType, string[] fields, Dictionary<string, string[]> held, Dictionary<string, string[]> given, string what, CancellationToken cancellationToken)
    {
        int written = 0, unchanged = 0, retired = 0;
        foreach (var (key, values) in given.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (held.TryGetValue(key, out var current) && current.SequenceEqual(values))
            {
                unchanged++;
                continue;
            }

            await _forms.FileAsync(formType, key, fields, values, $"Filing a {what}", cancellationToken).ConfigureAwait(false);
            written++;
        }

        foreach (var key in held.Keys.Where(k => !given.ContainsKey(k)).Order(StringComparer.Ordinal))
        {
            await _forms.RetireAsync(formType, key, $"Retiring a {what}", cancellationToken).ConfigureAwait(false);
            retired++;
        }

        await _forms.ProjectAsync(formType, $"Projecting the {what}s", cancellationToken).ConfigureAwait(false);
        return new RecordWrite(written, unchanged, retired);
    }
}

/// <summary>What a pass did to one of the identity store's form types.</summary>
/// <param name="Written">Records sent — new ones, and corrections of held ones.</param>
/// <param name="Unchanged">Records the store already held as given, not sent again.</param>
/// <param name="Retired">Held records the pass did not give, taken out.</param>
public sealed record RecordWrite(int Written, int Unchanged, int Retired);

/// <summary>What an identity holds to be the same thing as one entity.</summary>
/// <param name="Members">The IRIs of the other entities in its thing.</param>
/// <param name="SkippedDocuments">How many documents the projection skipped, so members may be missing; 0 when it holds them all.</param>
public sealed record SameAs(IReadOnlyList<string> Members, int SkippedDocuments)
{
    /// <summary>Whether members may be missing.</summary>
    public bool Incomplete => SkippedDocuments > 0;
}

/// <summary>What a pass did to the identity store.</summary>
/// <param name="Canonical">The canonical ids.</param>
/// <param name="Members">The cross-references.</param>
/// <param name="Grounds">The grounds.</param>
public sealed record IdentityWrite(RecordWrite Canonical, RecordWrite Members, RecordWrite Grounds)
{
    /// <summary>Canonical ids issued by this pass, for things found for the first time or split off.</summary>
    public int Issued { get; init; }

    /// <summary>Canonical ids retired because their thing merged into an older one.</summary>
    public int Merged { get; init; }
}

/// <summary>How the identity store compares with what a pass would find now.</summary>
/// <param name="Matching">Things held with exactly the members found.</param>
/// <param name="Missing">Things found that share no member with any held one.</param>
/// <param name="Differing">Things found that the store holds with other members — merged, split, or grown since.</param>
/// <param name="Extra">Things held that share no member with anything found.</param>
/// <param name="GroundsMissing">Pairs found that the store holds no ground for.</param>
/// <param name="GroundsExtra">Grounds held for pairs no longer found.</param>
/// <param name="SkippedDocuments">Documents the store's projections skipped.</param>
public sealed record IdentityComparison(int Matching, int Missing, int Differing, int Extra, int GroundsMissing, int GroundsExtra, int SkippedDocuments)
{
    /// <summary>The store holds what a pass would make it hold, up to the canonical ids, which a pass carries on.</summary>
    public bool Agrees => Missing == 0 && Differing == 0 && Extra == 0 && GroundsMissing == 0 && GroundsExtra == 0 && SkippedDocuments == 0;
}

/// <summary>The group's identity store refused a pass, or holds what saem did not write.</summary>
public sealed class IdentityStoreException : Exception
{
    public IdentityStoreException()
    {
    }

    public IdentityStoreException(string message)
        : base(message)
    {
    }

    public IdentityStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
