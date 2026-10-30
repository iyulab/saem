using System.Text.Json;
using System.Text.Json.Serialization;

namespace Saem.Contracts;

/// <summary>
/// An entity, named by its declared type and its key in the system it stands on.
/// </summary>
/// <param name="Type">The entity type, as the ontology declares it.</param>
/// <param name="Key">The entity's key in its source system.</param>
public sealed record EntityKey(string Type, string Key);

/// <summary>
/// A lookup: start at one entity, walk declared relations, and read declared fields of the entity the
/// walk ends at. Every term is the ontology's — the caller composes the question; saem runs it against
/// the source systems with the credentials of the person the caller acts for, and does not interpret it.
/// </summary>
/// <param name="Start">The entity the walk starts from — by its key, or by a field's value.</param>
/// <param name="Path">The relations to follow, in order; empty reads the start entity itself.</param>
/// <param name="Read">The fields to read on the entity the walk ends at.</param>
public sealed record LookupCall(LookupStart Start, IReadOnlyList<string> Path, IReadOnlyList<string> Read);

/// <summary>
/// Where a walk starts: an entity of a declared type, named either by its key or by the value of one of its
/// declared fields. A field may hold the same value on several entities; then the lookup answers with the
/// <see cref="Candidates"/> the person can see, and the caller starts again from one of them by key.
/// </summary>
public sealed record LookupStart
{
    /// <param name="type">The entity type, as the ontology declares it.</param>
    /// <param name="key">The entity's key in its source system; given when <paramref name="where"/> is not.</param>
    /// <param name="where">A declared field and the value it holds; given when <paramref name="key"/> is not.</param>
    [JsonConstructor]
    public LookupStart(string type, string? key = null, FieldMatch? where = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        if ((key is null) == (where is null))
        {
            throw new ArgumentException("A start names its entity by a key or by a field's value — exactly one of the two.", nameof(key));
        }

        Type = type;
        Key = key;
        Where = where;
    }

    /// <summary>The entity type.</summary>
    public string Type { get; }

    /// <summary>The entity's key, or null when it is named by <see cref="Where"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Key { get; }

    /// <summary>The field and value the entity is named by, or null when it is named by <see cref="Key"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FieldMatch? Where { get; }

    /// <summary>A start at a known entity.</summary>
    public static implicit operator LookupStart(EntityKey entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new LookupStart(entity.Type, entity.Key);
    }
}

/// <summary>A declared field and the value an entity holds in it, compared as the source compares it.</summary>
/// <param name="Field">The field, as the entity type declares it.</param>
/// <param name="Value">The value, as text.</param>
public sealed record FieldMatch(string Field, string Value);

/// <summary>
/// What a lookup answers: facts, each with where it came from — or a refusal saying where the walk
/// found nothing — or, for a start named by a field's value that several entities hold, those entities
/// to choose from. Exactly one of the three. On the wire the case is named in <c>kind</c>
/// (<see cref="ContractJson"/>); <see cref="ContractSchema"/> is the same shape as a JSON Schema.
/// <para>
/// An answer is not a snapshot. Each source record says when it was read (<see cref="SourceRecord.ReadAt"/>),
/// and a walk that crosses systems reads them one after another, so two facts of one answer can be of
/// different moments.
/// </para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Answered), "answered")]
[JsonDerivedType(typeof(Refused), "refused")]
[JsonDerivedType(typeof(Candidates), "candidates")]
public abstract record LookupAnswer
{
    private protected LookupAnswer()
    {
    }

    /// <summary>
    /// Whose view the answer is: every source was read as this person, so what it states — and where it
    /// refuses — holds for them and not for whoever the answer is passed on to. Set by the surface that
    /// knows who called; null where none does.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Viewer? AnsweredFor { get; init; }

    /// <summary>
    /// The version of the ontology the lookup was planned against — the same value the tool listing carries,
    /// so a caller can tell the vocabulary it composed the call from is the one it was answered in. Null
    /// where the walk did not say.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CatalogVersion { get; init; }

    /// <summary>
    /// The group partitions a relation walked backwards was read from, each with what it says about the
    /// answer's completeness; null when no relation was walked backwards.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PartitionRead>? Partitions { get; init; }
}

/// <summary>
/// One group partition a relation walked backwards was read from, and what it says beyond the facts. Its
/// edges only find candidates — every entity stated was read again as the person — so a partition can know
/// of more than the answer states, and it says so here without saying what or how many.
/// </summary>
/// <param name="Name">The group.</param>
/// <param name="AsOf">When the partition's edges were read from the sources; null when it does not say.</param>
/// <param name="Blocked">
/// The partition led to entities the person's own reads did not show, or no longer show leading here:
/// they changed after <paramref name="AsOf"/>, or are not visible to this person — saem cannot tell which.
/// </param>
/// <param name="Incomplete">The partition was built without some of its source documents, so entities may be missing from it.</param>
public sealed record PartitionRead(string Name, DateTimeOffset? AsOf, bool Blocked, bool Incomplete)
{
    /// <summary>
    /// What <see cref="Blocked"/> and <see cref="Incomplete"/> mean, in words a consumer can render as they
    /// are; empty when the partition says nothing beyond the facts.
    /// </summary>
    public IReadOnlyList<string> Statements =>
    [
        .. Blocked
            ? ["Among what this group's relations led to when they were built, something is not visible to this person now: it changed since, or it is outside what this person may see. saem cannot tell which, and says nothing of what or how many."]
            : Array.Empty<string>(),
        .. Incomplete
            ? ["This group's relations were built without some of their source documents, so entities that lead here may be missing from the answer."]
            : Array.Empty<string>(),
    ];
}

/// <summary>The person a lookup was run as, and who asked on their behalf.</summary>
/// <param name="Subject">The person — the <c>sub</c> of the token the lookup was called with.</param>
/// <param name="ActedBy">The agent that called for them (the token's <c>act</c>), or null when they called directly.</param>
public sealed record Viewer(string Subject, string? ActedBy)
{
    /// <inheritdoc cref="Viewer" path="/param[@name='ActedBy']"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActedBy { get; init; } = ActedBy;
}

/// <summary>A lookup that found something: at least one fact.</summary>
public sealed record Answered : LookupAnswer
{
    /// <param name="claims">The facts found; at least one — an answer with nothing in it is a refusal.</param>
    public Answered(IReadOnlyList<Claim> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        if (claims.Count == 0)
        {
            throw new ArgumentException("An answer carries at least one claim; a lookup that found nothing is refused.", nameof(claims));
        }

        Claims = claims;
    }

    /// <summary>The facts found.</summary>
    public IReadOnlyList<Claim> Claims { get; }
}

/// <summary>A lookup that found nothing to state, and says where.</summary>
/// <param name="Refusal">Why nothing is stated, and where the walk stopped.</param>
public sealed record Refused(Refusal Refusal) : LookupAnswer;

/// <summary>
/// A start named by a field's value that more than one entity visible to the person holds: the walk did
/// not start, and these are the entities to start from by key. Only rows the person's own read returned.
/// </summary>
public sealed record Candidates : LookupAnswer
{
    /// <param name="entities">The entities; at least two — one would have been walked from, none is a refusal.</param>
    public Candidates(IReadOnlyList<Candidate> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count < 2)
        {
            throw new ArgumentException("Candidates are two or more; a single match is walked from, and none is refused.", nameof(entities));
        }

        Entities = entities;
    }

    /// <summary>The entities to choose from.</summary>
    public IReadOnlyList<Candidate> Entities { get; }
}

/// <summary>An entity a start's field value matched: its key, and the record that names it.</summary>
/// <param name="Entity">The entity — its type and the key to start from.</param>
/// <param name="Source">The record it was read from, cited as its type declares (the name a person recognises it by).</param>
public sealed record Candidate(EntityKey Entity, SourceRecord Source);

/// <summary>
/// One fact: a field of an entity and its value, with the source records the value was read from and
/// the relations the walk followed to reach the entity. A fact without a source cannot be built.
/// </summary>
public sealed record Claim
{
    /// <param name="entity">The entity the fact is about.</param>
    /// <param name="field">The field read.</param>
    /// <param name="value">The value as the source system holds it; <see cref="JsonValueKind.Null"/> when the field is empty.</param>
    /// <param name="sources">The records the value was read from; at least one.</param>
    /// <param name="path">The relations followed from the start entity, in order; empty for the start entity itself.</param>
    public Claim(EntityKey entity, string field, JsonElement value, IReadOnlyList<SourceRecord> sources, IReadOnlyList<Hop> path)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrEmpty(field);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(path);
        if (sources.Count == 0)
        {
            throw new ArgumentException("A claim names at least one source record; a value nobody can trace is not stated.", nameof(sources));
        }

        Entity = entity;
        Field = field;
        Value = value;
        Sources = sources;
        Path = path;
    }

    /// <summary>The entity the fact is about.</summary>
    public EntityKey Entity { get; }

    /// <summary>The field read.</summary>
    public string Field { get; }

    /// <summary>The value as the source system holds it.</summary>
    public JsonElement Value { get; }

    /// <summary>The records the value was read from.</summary>
    public IReadOnlyList<SourceRecord> Sources { get; }

    /// <summary>The relations followed from the start entity.</summary>
    public IReadOnlyList<Hop> Path { get; }

    /// <summary>
    /// What kind of value the field holds, as the ontology declares it — null where it declares none. A
    /// <see cref="FieldKind.FreeText"/> value is whatever a person typed, and may carry words addressed to the
    /// model reading it: a consumer holds it apart from instructions.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FieldKind? FieldKind { get; init; }
}

/// <summary>What kind of value a field holds. On the wire each kind travels by its camelCase name.</summary>
public enum FieldKind
{
    /// <summary>A short text — a name, a code, a status.</summary>
    [JsonStringEnumMemberName("text")]
    Text,

    /// <summary>Text a person wrote freely — a note, a description — which may carry anything, instructions to a model included.</summary>
    [JsonStringEnumMemberName("freeText")]
    FreeText,

    /// <summary>A number.</summary>
    [JsonStringEnumMemberName("number")]
    Number,

    /// <summary>A date or a moment.</summary>
    [JsonStringEnumMemberName("date")]
    Date,

    /// <summary>The key of an entity — of this type, or of the type a relation through this field leads to.</summary>
    [JsonStringEnumMemberName("key")]
    Key,
}



/// <summary>
/// A record in a source system: where a value can be checked. An identifier, not a copy of the record.
/// </summary>
/// <param name="System">The source system, as the deployment names it.</param>
/// <param name="Table">The table (or equivalent) in that system.</param>
/// <param name="Record">
/// The record read, as the ontology names it: the columns the entity's type declares as its record,
/// joined with <c>/</c> — or, where it declares none, the row's key.
/// </param>
public sealed record SourceRecord(string System, string Table, string Record)
{
    /// <summary>
    /// When saem read the record from its source to state the value — the moment the value is as of. Every
    /// source of an answer is read at the moment the lookup is asked, as the person, so this is the age of
    /// what the answer says; null where the record is named without having been read (an edge built ahead
    /// of time names the record it was built from).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ReadAt { get; init; }

    /// <summary>
    /// Where the table is a projection of documents kept elsewhere (a form's records), and that projection is
    /// not current: the value is what the projection holds, and the documents may say otherwise. Null when the
    /// table is no projection, or its projection is current.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceProjection? Projection { get; init; }
}

/// <summary>
/// How far a projected table is behind the documents it is projected from — said only when it is behind.
/// On the wire each state travels by its camelCase name.
/// </summary>
public enum SourceProjection
{
    /// <summary>The documents have no projected table yet: nothing projected can be read.</summary>
    [JsonStringEnumMemberName("notProjected")]
    NotProjected,

    /// <summary>Documents, or their declaration, changed after the projection ran: rows may be missing or out of date.</summary>
    [JsonStringEnumMemberName("stale")]
    Stale,

    /// <summary>A rebuild of the projection failed, so nothing vouches for its rows.</summary>
    [JsonStringEnumMemberName("unverified")]
    Unverified,
}

/// <summary>Who settles a pair an identity finds. On the wire each travels by its camelCase name.</summary>
public enum IdentityDecision
{
    /// <summary>The rule settles it: a pair it finds is one entity until a person says otherwise.</summary>
    [JsonStringEnumMemberName("automatic")]
    Automatic,

    /// <summary>A person settles it: a pair the rule finds is a candidate until someone confirms it.</summary>
    [JsonStringEnumMemberName("confirmed")]
    Confirmed,
}

/// <summary>One relation followed: from one entity to the next — or an identity crossed, from an entity to one the identity says is the same thing.</summary>
/// <param name="Relation">The relation, as the ontology declares it — its inverse's name, when it was walked backwards; or the identity's name, when one was crossed.</param>
/// <param name="From">The entity the relation was followed from.</param>
/// <param name="To">The entity it led to.</param>
public sealed record Hop(string Relation, EntityKey From, EntityKey To)
{
    /// <summary>
    /// The group whose materialized relations the hop was read from, when it was walked backwards; null
    /// when the hop followed a field read from the source. A relation walked backwards is answered from
    /// the edges built ahead of time for each of the person's groups, partition by partition and never
    /// merged, so the hop says which partition led to the entity.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Partition { get; init; }

    /// <summary>
    /// When <see cref="Partition"/>'s edges were read from the sources — the start of the pass that built
    /// them; null when the hop followed a field, or the partition does not say. The entity the hop led to was
    /// read again as the person, so what is stated of it is current; what the time bounds is the walk: an
    /// entity that came to lead here after it has no edge yet, and is not among the answers.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? PartitionAsOf { get; init; }

    /// <summary>
    /// When the hop crossed an identity, who said the two are one thing: its rule (<see cref="IdentityDecision.Automatic"/> —
    /// and no person has said otherwise) or a person (<see cref="IdentityDecision.Confirmed"/>); null when the hop followed a
    /// relation. The grounds the pair was judged on are not stated.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IdentityDecision? Identity { get; init; }

    /// <summary>
    /// The row the person read at this hop, as the person: for a relation, the row that holds the field naming the
    /// entity at the other end — so it is the record of <see cref="From"/> when the relation was followed forwards, and of
    /// <see cref="To"/> when it was walked backwards; for an identity, the row of the entity crossed to. It is what the
    /// source showed this person a moment ago, never what an edge built ahead of time recorded: that may have changed since,
    /// and the person may no longer be allowed to open it. Null where a hop does not say.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceRecord? Source { get; init; }
}

/// <summary>
/// Why a lookup states nothing, and where its walk stopped. A source that enforces its own permissions
/// returns no rows both for what does not exist and for what the person may not see, and saem cannot
/// tell the two apart — so a refusal never says "does not exist".
/// </summary>
/// <param name="Reason">Why nothing is stated.</param>
/// <param name="AtHop">Where the walk stopped: 0 for the start entity, n for the n-th relation of the path.</param>
/// <param name="Relation">The relation being followed when the walk stopped; null at the start entity.</param>
/// <param name="System">The source system that returned nothing — or, for a relation walked backwards, the one its entities stand on.</param>
public sealed record Refusal(RefusalReason Reason, int AtHop, string? Relation, string System)
{
    /// <summary>
    /// Where the table the walk found nothing in is a projection of documents kept elsewhere and is behind
    /// them: what was not found there may be in documents not yet projected. Null otherwise.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceProjection? Projection { get; init; }

    /// <summary>
    /// What a refusal means, worded so it cannot be read as absence. Carried in every refusal so a
    /// consumer that renders it has the words in hand.
    /// </summary>
    public string Statement => Reason switch
    {
        RefusalReason.NoVisibleResult =>
            "The source returned no rows to this person. Whether no such rows exist or they are not visible to this person, saem cannot tell.",
        RefusalReason.NoMaterializedPartition =>
            "This relation is answered from relations built ahead of time for the groups a person is in, and this person is in none that has them. Nothing is said about whether such entities exist.",
        _ => throw new InvalidOperationException($"No statement for {Reason}."),
    };
}

/// <summary>Why a lookup states nothing. On the wire each reason travels by its camelCase name.</summary>
public enum RefusalReason
{
    /// <summary>
    /// The start entity, or a relation on the path, returned no rows under the person's own access.
    /// Not "not found": the source may hold rows this person cannot see.
    /// </summary>
    [JsonStringEnumMemberName("noVisibleResult")]
    NoVisibleResult,

    /// <summary>
    /// The path walks a relation backwards, which is answered from the edges materialized for the
    /// person's groups, and the person is in no group the deployment keeps them for. <c>system</c> names
    /// the system the entities walked to stand on; nothing was read from it.
    /// </summary>
    [JsonStringEnumMemberName("noMaterializedPartition")]
    NoMaterializedPartition,
}
