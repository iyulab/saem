using System.Globalization;
using System.Numerics;
using System.Text.Json.Serialization;
using Saem.Contracts;

namespace Saem.Ontology;

/// <summary>
/// One declaration of the ontology: an entity type or a relation. Each is a document of its own, so
/// that the same declarations can later be held one document apiece wherever the ontology is kept —
/// the shape does not change with the place. On the wire the kind travels in <c>kind</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(EntityTypeDeclaration), "entityType")]
[JsonDerivedType(typeof(RelationDeclaration), "relation")]
public abstract record Declaration
{
    private protected Declaration()
    {
    }

    /// <summary>The declared name — an entity type's or a relation's, unique among its kind.</summary>
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
