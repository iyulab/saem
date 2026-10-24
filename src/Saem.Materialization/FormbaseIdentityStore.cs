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
/// one no longer given is retired. An identity whose pairs a person settles (<c>confirmed</c>) files grounds only — its
/// pairs are candidates, and no canonical id is issued for them until a person has said they are one.
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
        int issued = 0, merged = 0;
        foreach (var identity in ontology.Identities.Values.OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            var given = found.GetValueOrDefault(identity.Name) ?? [];
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

            if (identity.Decision == Ontology.IdentityDecision.Confirmed)
            {
                continue;
            }

            var held = HeldOf(identity.Name, heldCanonical, heldMembers);
            var succession = IdentitySuccession.Succeed(held, given, now, _issue);
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

    /// <summary>The things each identity holds now, by canonical id, each with its members — every one of them, or a failure when a projection skipped documents.</summary>
    public async Task<IReadOnlyList<CanonicalIdentity>> ReadAsync(CancellationToken cancellationToken = default)
    {
        var heldCanonical = await _forms.HeldAsync(CanonicalFormType, "", CanonicalFields, cancellationToken).ConfigureAwait(false);
        var heldMembers = await _forms.HeldAsync(MemberFormType, "", MemberFields, cancellationToken).ConfigureAwait(false);
        return [.. heldCanonical.Values.Select(c => c[0]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .SelectMany(identity => HeldOf(identity, heldCanonical, heldMembers))];
    }

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
