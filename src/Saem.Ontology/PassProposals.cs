using System.Text;
using System.Text.Json;

namespace Saem.Ontology;

/// <summary>
/// The declarations one authoring pass proposed, kept beside its draft until a person has reviewed it:
/// the draft file is edited in the review, so what was proposed would otherwise be lost.
/// </summary>
/// <param name="Pass">The pass's id — every settlement of its proposals carries it as their batch.</param>
/// <param name="Added">The declarations the pass proposed — the ones its draft adds to the catalog in force.</param>
/// <param name="Generation">What the pass was made with, when the run knows it; proposals kept before it was recorded have none.</param>
public sealed record ProposedDeclarations(string Pass, IReadOnlyList<Declaration> Added, PassGeneration? Generation = null)
{
    /// <summary>The name of a declaration's kind as it travels in the catalog file's <c>kind</c>.</summary>
    public static string KindOf(Declaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        return declaration switch
        {
            EntityTypeDeclaration => "entityType",
            RelationDeclaration => "relation",
            IdentityDeclaration => "identity",
            LinkageDeclaration => "linkage",
            GroupingDeclaration => "grouping",
            _ => declaration.GetType().Name,
        };
    }

    /// <summary>A declaration's id among a pass's proposals: its kind and its name, <c>entityType:WorkOrder</c>.</summary>
    public static string IdOf(Declaration declaration) => $"{KindOf(declaration)}:{declaration.Name}";

    /// <summary>The proposals as a JSON object: the pass, the declarations in the catalog file's form, and what the pass was made with.</summary>
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("pass", Pass);
            writer.WritePropertyName("added");
            JsonSerializer.Serialize(writer, Added, OntologyJson.Default.IReadOnlyListDeclaration);
            if (Generation is not null)
            {
                writer.WritePropertyName("generation");
                Generation.Write(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>Reads what <see cref="ToJson"/> wrote.</summary>
    /// <exception cref="JsonException">The text is not a pass's proposals.</exception>
    public static ProposedDeclarations Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("pass", out var pass) || pass.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(pass.GetString())
            || !root.TryGetProperty("added", out var added) || added.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("A pass's proposals are an object with a \"pass\" id and an \"added\" array of declarations.");
        }

        var generation = root.TryGetProperty("generation", out var made) && made.ValueKind != JsonValueKind.Null ? PassGeneration.Read(made) : null;
        return new ProposedDeclarations(pass.GetString()!, added.Deserialize(OntologyJson.Default.IReadOnlyListDeclaration) ?? [], generation);
    }
}
