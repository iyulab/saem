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
/// <param name="Evidence">For each proposal, by its id (<see cref="IdOf"/>), the records the pass drew it from; proposals kept before the pass recorded them have none.</param>
public sealed record ProposedDeclarations(
    string Pass,
    IReadOnlyList<Declaration> Added,
    PassGeneration? Generation = null,
    IReadOnlyDictionary<string, IReadOnlyList<PassCitation>>? Evidence = null)
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

            if (Evidence is { Count: > 0 })
            {
                writer.WriteStartObject("evidence");
                foreach (var (id, citations) in Evidence.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    writer.WriteStartArray(id);
                    foreach (var citation in citations)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("record", citation.Record);
                        if (citation.Subject is not null)
                        {
                            writer.WriteString("subject", citation.Subject);
                        }

                        if (citation.Fields is { Count: > 0 })
                        {
                            writer.WriteStartObject("fields");
                            foreach (var (name, value) in citation.Fields)
                            {
                                writer.WriteString(name, value);
                            }

                            writer.WriteEndObject();
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
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
        return new ProposedDeclarations(pass.GetString()!, added.Deserialize(OntologyJson.Default.IReadOnlyListDeclaration) ?? [], generation, ReadEvidence(root));
    }

    private static Dictionary<string, IReadOnlyList<PassCitation>>? ReadEvidence(JsonElement root)
    {
        if (!root.TryGetProperty("evidence", out var evidence) || evidence.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (evidence.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A pass's \"evidence\" is an object of proposal id -> the records it was drawn from.");
        }

        var read = new Dictionary<string, IReadOnlyList<PassCitation>>(StringComparer.Ordinal);
        foreach (var proposal in evidence.EnumerateObject())
        {
            if (proposal.Value.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException($"The evidence of '{proposal.Name}' is an array of records.");
            }

            var citations = new List<PassCitation>();
            foreach (var citation in proposal.Value.EnumerateArray())
            {
                if (citation.ValueKind != JsonValueKind.Object
                    || !citation.TryGetProperty("record", out var record) || record.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(record.GetString()))
                {
                    throw new JsonException($"Each record cited for '{proposal.Name}' is an object with a \"record\" id.");
                }

                var subject = citation.TryGetProperty("subject", out var named) && named.ValueKind == JsonValueKind.String ? named.GetString() : null;
                Dictionary<string, string?>? fields = null;
                if (citation.TryGetProperty("fields", out var drawn) && drawn.ValueKind == JsonValueKind.Object)
                {
                    fields = drawn.EnumerateObject().ToDictionary(f => f.Name, f => f.Value.ValueKind == JsonValueKind.String ? f.Value.GetString() : null, StringComparer.Ordinal);
                }

                citations.Add(new PassCitation(record.GetString()!, subject, fields));
            }

            read[proposal.Name] = citations;
        }

        return read;
    }
}

/// <summary>A record a proposal was drawn from: the id the source gave it and the subject (form type or table) it is of, when the pass knew it.</summary>
/// <param name="Record">The record's id as the sampled record carried it — for a Formbase document, its document id.</param>
/// <param name="Subject">The subject the record is of; absent when the pass did not know it.</param>
public sealed record PassCitation(string Record, string? Subject, IReadOnlyDictionary<string, string?>? Fields = null);
