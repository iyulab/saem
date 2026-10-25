using System.Globalization;
using System.Numerics;
using System.Text.Json.Serialization;
using Saem.Contracts;

namespace Saem.Ontology;

/// <summary>
/// One declaration of the ontology: an entity type, a relation, an identity, a linkage rule or a grouping. Each is a
/// document of its own, so that the same declarations can later be held one document apiece wherever the ontology is
/// kept — the shape does not change with the place. On the wire the kind travels in <c>kind</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(EntityTypeDeclaration), "entityType")]
[JsonDerivedType(typeof(RelationDeclaration), "relation")]
[JsonDerivedType(typeof(IdentityDeclaration), "identity")]
[JsonDerivedType(typeof(LinkageDeclaration), "linkage")]
[JsonDerivedType(typeof(GroupingDeclaration), "grouping")]
public abstract record Declaration
{
    private protected Declaration()
    {
    }

    /// <summary>The declared name, unique among its kind — and a grouping's also among the entity types.</summary>
    public abstract string Name { get; }
}

/// <summary>
/// An entity type and the source table it stands on: which system, which table, which column keys an
/// entity, and which columns are its fields.
/// </summary>
/// <param name="Name">The type's name — what a lookup starts from and a claim is about.</param>
/// <param name="System">The source system, as the deployment names it.</param>
/// <param name="Table">The table (or equivalent) in that system.</param>
/// <param name="Key">The column whose value is an entity's key.</param>
/// <param name="Fields">The type's fields, each mapped to the column that holds it.</param>
/// <param name="Record">
/// The columns whose values name the source record a row is — what an answer's source cites — joined
/// with <c>/</c> when there are several. Omitted, the key names it. Declared where the two differ: a
/// table whose rows are copies of records kept elsewhere (a form's projected rows name the document
/// they were projected from) is keyed by what an entity is, and cited by what was read.
/// </param>
public sealed record EntityTypeDeclaration(
    string Name,
    string System,
    string Table,
    string Key,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<string>? Record = null) : Declaration
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string Name { get; } = Name;

    /// <inheritdoc cref="EntityTypeDeclaration" path="/param[@name='Record']"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Record { get; init; } = Record;

    /// <summary>
    /// What kind of value the key is, which decides when two spellings name the same entity. Omitted, text:
    /// two keys are the same only when they are the same text.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public KeyKind? KeyKind { get; init; }

    /// <summary>
    /// What kind of value each field holds, by field name — for the fields that declare one. Omitted, a field's
    /// kind is not declared.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, FieldKind>? Kinds { get; init; }

    /// <summary>
    /// The column the source sets when a row changes — an <c>updated_at</c> it keeps itself. Declared, a pass may read
    /// only the rows changed since the last one instead of the whole table; omitted, every pass reads it whole. It says
    /// nothing a lookup answers: no claim cites it, and a pass that reads it never trusts it to see a row deleted.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChangedAt { get; init; }

    /// <summary>
    /// The one spelling of <paramref name="key"/> every comparison of this type's keys uses: a number key is
    /// compared as the integer it spells, so <c>0001</c>, <c>+1</c> and <c>1</c> name one entity; a text key is
    /// itself. A number key that spells no integer is left as written — it names no entity a source holds.
    /// </summary>
    public string Normalize(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return KeyKind == global::Saem.Ontology.KeyKind.Number && BigInteger.TryParse(key.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : key;
    }
}

/// <summary>What kind of value an entity type's key is. On the wire each kind travels by its camelCase name.</summary>
public enum KeyKind
{
    /// <summary>Text: two keys are the same only when they are the same text.</summary>
    [JsonStringEnumMemberName("text")]
    Text,

    /// <summary>An integer: two keys are the same when they spell the same number, whatever leading zeros or sign.</summary>
    [JsonStringEnumMemberName("number")]
    Number,
}

/// <summary>
/// A relation: from an entity of one type, through one of its fields, to the entity of another type
/// whose key that field holds. The two types may stand on different systems — the relation is the
/// ontology's statement that the value in one system names an entity in the other.
/// </summary>
/// <param name="Name">The relation's name — what a lookup's path follows.</param>
/// <param name="From">The entity type the relation is followed from.</param>
/// <param name="Via">The field of <paramref name="From"/> whose value is the key of the entity it leads to.</param>
/// <param name="To">The entity type the relation leads to.</param>
/// <param name="Inverse">
/// The name of the relation walked backwards — from an entity of <paramref name="To"/> to every entity of
/// <paramref name="From"/> that leads to it (<c>owl:inverseOf</c>). Omitted, the relation is walked forwards
/// only. Walking it backwards needs every row of <paramref name="From"/> that names the entity, which no
/// single read answers, so it is answered from the edges materialized for the person's groups.
/// </param>
public sealed record RelationDeclaration(string Name, string From, string Via, string To, string? Inverse = null) : Declaration
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string Name { get; } = Name;

    /// <inheritdoc cref="RelationDeclaration" path="/param[@name='Inverse']"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Inverse { get; init; } = Inverse;
}

/// <summary>
/// An identity: the statement that an entity of one type and an entity of another can be one and the same thing
/// — a customer in the ERP and the same customer in the CRM — and how to tell when they are. Each pair it finds
/// is kept as a canonical id with cross-references and the grounds it was judged on, never as an
/// <c>owl:sameAs</c> chain, whose closure joins things no one judged the same. Both types may be one type, which
/// declares how duplicates inside one table are found.
/// </summary>
/// <param name="Name">The identity's name, unique among identities.</param>
/// <param name="First">One of the two entity types.</param>
/// <param name="Second">The other — or the same type again, for duplicates within one.</param>
/// <param name="Decision">Who settles a pair the identity finds: the rule alone, or a person.</param>
public sealed record IdentityDeclaration(string Name, string First, string Second, IdentityDecision Decision) : Declaration
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string Name { get; } = Name;

    /// <summary>
    /// The fields whose values must all be equal for two entities to be one — a deterministic match. Declared
    /// instead of <see cref="Linkage"/>, never with it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FieldPair>? Match { get; init; }

    /// <summary>
    /// The name of the linkage rule (<see cref="LinkageDeclaration"/>) that scores two entities as one — a probabilistic
    /// match, for records no shared key joins. Declared instead of <see cref="Match"/>, never with it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Linkage { get; init; }
}

/// <summary>A field of an identity's first type and the field of its second type that must hold the same value.</summary>
/// <param name="First">The field of <see cref="IdentityDeclaration.First"/>.</param>
/// <param name="Second">The field of <see cref="IdentityDeclaration.Second"/>.</param>
public sealed record FieldPair(string First, string Second);

/// <summary>
/// A linkage rule: how two entities that share no key are scored as one thing — the fields compared, each by a method
/// and the similarities at which its values agree, and the scores at which a pair is one thing or two (Fellegi–Sunter).
/// It is the ontology's statement of what "the same" means for those records, so it is declared, versioned and
/// reviewed like any other; an identity names it (<see cref="IdentityDeclaration.Linkage"/>). The rule names no type —
/// one rule serves every identity whose two types carry its fields, the first field of each pair on the identity's
/// first type. How much each field's agreement weighs is not declared: it is fitted to the records a group sees.
/// </summary>
/// <param name="Name">The rule's name, unique among linkage rules.</param>
/// <param name="Compare">The pairs of fields compared, each with how; at least one.</param>
public sealed record LinkageDeclaration(string Name, IReadOnlyList<LinkageComparison> Compare) : Declaration
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string Name { get; } = Name;

    /// <summary>
    /// The score — a log-likelihood ratio — at or above which a pair is one thing. Omitted, the scorer's own default.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MatchThreshold { get; init; }

    /// <summary>
    /// The score at or below which a pair is two things. Between the two thresholds a pair is undecided: an identity a
    /// person settles puts it to that person; one the rule settles leaves the two apart. Omitted, the scorer's default.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? NonMatchThreshold { get; init; }
}

/// <summary>A field of an identity's first type compared with a field of its second type, and how.</summary>
/// <param name="First">The field of the identity's first type.</param>
/// <param name="Second">The field of the identity's second type.</param>
/// <param name="Method">How two values are compared. Omitted, they must be equal.</param>
/// <param name="Thresholds">
/// For a similarity method, the similarities at or above which two values agree, highest first, each within [0, 1]:
/// one is agree or disagree, several give a near miss its own weight. Omitted for <see cref="LinkageMethod.Exact"/>,
/// which has none; for Jaro-Winkler and Monge-Elkan omitted means the scorer's default; token Jaccard needs one.
/// </param>
public sealed record LinkageComparison(
    string First,
    string Second,
    LinkageMethod Method = LinkageMethod.Exact,
    IReadOnlyList<double>? Thresholds = null)
{
    /// <inheritdoc cref="LinkageComparison" path="/param[@name='Thresholds']"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<double>? Thresholds { get; init; } = Thresholds;
}

/// <summary>How a linkage rule compares two values of a field. On the wire each method travels by its camelCase name.</summary>
public enum LinkageMethod
{
    /// <summary>The values, case and surrounding space aside, are equal.</summary>
    [JsonStringEnumMemberName("exact")]
    Exact,

    /// <summary>Jaro-Winkler similarity over the whole value — for a name or a code that varies by a letter.</summary>
    [JsonStringEnumMemberName("jaroWinkler")]
    JaroWinkler,

    /// <summary>The share of distinct words the values have in common — for a title whose words are added or moved.</summary>
    [JsonStringEnumMemberName("tokenJaccard")]
    TokenJaccard,

    /// <summary>Each word matched to its closest counterpart by Jaro-Winkler — word order and a misspelt word count for little.</summary>
    [JsonStringEnumMemberName("mongeElkan")]
    MongeElkan,
}

/// <summary>
/// A grouping: a line, a fleet, a team — a thing that is nothing but its members. A group is the set of members
/// whose field holds the same value, and that value is the group's key; the members are entities the sources
/// hold, the group is computed from them. A grouping has no fields of its own and the shape leaves no place for
/// one: a fact about a group is a fact some source must hold, or saem would be authoring an entity of its own.
/// </summary>
/// <param name="Name">The grouping's name, unique among groupings and entity types alike.</param>
/// <param name="Members">The entity types whose entities are its members, each with the field naming its group.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GroupingDeclaration(string Name, IReadOnlyList<GroupMembership> Members) : Declaration
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string Name { get; } = Name;
}

/// <summary>One member type of a grouping: an entity belongs to the group its field <paramref name="By"/> names.</summary>
/// <param name="Type">The member entity type.</param>
/// <param name="By">The field of <paramref name="Type"/> whose value is the key of the member's group.</param>
public sealed record GroupMembership(string Type, string By);
