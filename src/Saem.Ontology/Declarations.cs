using System.Text.Json.Serialization;

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
public sealed record RelationDeclaration(string Name, string From, string Via, string To) : Declaration
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string Name { get; } = Name;
}
