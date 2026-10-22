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
/// <param name="Start">The entity the walk starts from.</param>
/// <param name="Path">The relations to follow, in order; empty reads the start entity itself.</param>
/// <param name="Read">The fields to read on the entity the walk ends at.</param>
public sealed record LookupCall(EntityKey Start, IReadOnlyList<string> Path, IReadOnlyList<string> Read);

/// <summary>
/// What a lookup answers: facts, each with where it came from — or a refusal saying where the walk
/// found nothing. Never both, never neither. On the wire the case is named in <c>kind</c>
/// (<see cref="ContractJson"/>).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Answered), "answered")]
[JsonDerivedType(typeof(Refused), "refused")]
public abstract record LookupAnswer
{
    private protected LookupAnswer()
    {
    }
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
public sealed record SourceRecord(string System, string Table, string Record);

/// <summary>One relation followed: from one entity to the next.</summary>
/// <param name="Relation">The relation, as the ontology declares it.</param>
/// <param name="From">The entity the relation was followed from.</param>
/// <param name="To">The entity it led to.</param>
public sealed record Hop(string Relation, EntityKey From, EntityKey To);

/// <summary>
/// Why a lookup states nothing, and where its walk stopped. A source that enforces its own permissions
/// returns no rows both for what does not exist and for what the person may not see, and saem cannot
/// tell the two apart — so a refusal never says "does not exist".
/// </summary>
/// <param name="Reason">Why nothing is stated.</param>
/// <param name="AtHop">Where the walk stopped: 0 for the start entity, n for the n-th relation of the path.</param>
/// <param name="Relation">The relation being followed when the walk stopped; null at the start entity.</param>
/// <param name="System">The source system that returned nothing.</param>
public sealed record Refusal(RefusalReason Reason, int AtHop, string? Relation, string System)
{
    /// <summary>
    /// What a refusal means, worded so it cannot be read as absence. Carried in every refusal so a
    /// consumer that renders it has the words in hand.
    /// </summary>
    public string Statement => Reason switch
    {
        RefusalReason.NoVisibleResult =>
            "The source returned no rows to this person. Whether no such rows exist or they are not visible to this person, saem cannot tell.",
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
}
