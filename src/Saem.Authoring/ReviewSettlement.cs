using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Saem.Ontology;

namespace Saem.Authoring;

/// <summary>
/// The declarations one authoring pass proposed, kept beside its draft until a person has reviewed it:
/// the draft file is edited in the review, so what was proposed would otherwise be lost.
/// </summary>
/// <param name="Pass">The pass's id — every settlement of its proposals carries it as their batch.</param>
/// <param name="Added">The declarations the pass proposed (<see cref="DeclarationDraft.Added"/>).</param>
/// <param name="Generation">What the pass was made with, when the run knows it; proposals kept before it was recorded have none.</param>
public sealed record ProposedDeclarations(string Pass, IReadOnlyList<Declaration> Added, PassGeneration? Generation = null)
{
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

/// <summary>
/// What a person did with a pass's proposals, as settled records in the form Gil reads
/// (<c>docs/settled-records.md</c> of Gil): one line per proposed declaration, read against the catalog the
/// review left in force. A proposal is a suggestion a person settled, so the records teach how often a
/// proposal of each kind is taken as it was — what a later promise about proposals is chosen from.
/// </summary>
/// <remarks>
/// <para>Each line is a record whose id is the declaration's kind and name (<c>entityType:WorkOrder</c>) and
/// whose batch is the pass. Two attributes are decided: <c>taken</c> — <c>yes</c> when the catalog holds a
/// declaration of that kind and name, <c>no</c> when it does not — and, for one taken, <c>declaration</c>, the
/// declaration as the catalog holds it. Both were suggested by the pass (<c>yes</c>, and the declaration as
/// proposed), which is not Gil: a value kept is <c>AcceptOther</c>, another value <c>CorrectOther</c>. The
/// other values describe the proposal — its kind, and where an entity type stands or what a relation joins —
/// so which proposals are taken can be keyed by them.</para>
/// <para>A declaration renamed in the review reads as one not taken; the declaration under its new name was
/// not proposed and has no line.</para>
/// </remarks>
public static class ReviewSettlement
{
    // A line is read by a JSON reader, never placed in a page: names in any script stay as written, and a
    // declaration kept as text needs only its quotes escaped.
    private static readonly JsonWriterOptions LineOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The settled-record lines for <paramref name="proposed"/> against <paramref name="inForce"/>.</summary>
    /// <param name="proposed">What the pass proposed.</param>
    /// <param name="inForce">The declarations the catalog holds after the review.</param>
    /// <param name="settledAt">When the review was settled.</param>
    /// <param name="settledBy">An opaque id of the person who settled it — never a name — or null when it is not kept.</param>
    public static IReadOnlyList<string> Lines(ProposedDeclarations proposed, IReadOnlyList<Declaration> inForce, DateTimeOffset settledAt, string? settledBy = null)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(inForce);

        var ticks = settledAt.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var lines = new List<string>(proposed.Added.Count);
        foreach (var declaration in proposed.Added)
        {
            var kind = Kind(declaration);
            var settled = inForce.FirstOrDefault(d => Kind(d) == kind && d.Name == declaration.Name);
            var suggestedText = Canonical(declaration);
            var settledText = settled is null ? null : Canonical(settled);

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, LineOptions))
            {
                writer.WriteStartObject();
                writer.WriteString("id", $"{kind}:{declaration.Name}");
                writer.WriteNumber("settled_at", ticks);

                writer.WriteStartObject("values");
                writer.WriteString("kind", kind);
                foreach (var (name, value) in Described(declaration))
                {
                    writer.WriteString(name, value);
                }

                writer.WriteString("taken", settled is null ? "no" : "yes");
                if (settledText is not null)
                {
                    writer.WriteString("declaration", settledText);
                }

                writer.WriteEndObject();

                writer.WriteStartObject("settlements");
                writer.WriteString("taken", settled is null ? "CorrectOther" : "AcceptOther");
                if (settledText is not null)
                {
                    writer.WriteString("declaration", settledText == suggestedText ? "AcceptOther" : "CorrectOther");
                }

                writer.WriteEndObject();

                writer.WriteString("batch", proposed.Pass);

                writer.WriteStartObject("suggested");
                writer.WriteString("taken", "yes");
                if (settledText is not null)
                {
                    writer.WriteString("declaration", suggestedText);
                }

                writer.WriteEndObject();

                if (settledBy is not null)
                {
                    writer.WriteStartObject("settled_by");
                    writer.WriteString("taken", settledBy);
                    if (settledText is not null)
                    {
                        writer.WriteString("declaration", settledBy);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            lines.Add(Encoding.UTF8.GetString(buffer.ToArray()));
        }

        return lines;
    }

    private static string Kind(Declaration declaration) => declaration switch
    {
        EntityTypeDeclaration => "entityType",
        RelationDeclaration => "relation",
        IdentityDeclaration => "identity",
        LinkageDeclaration => "linkage",
        GroupingDeclaration => "grouping",
        _ => declaration.GetType().Name,
    };

    private static IEnumerable<(string Name, string Value)> Described(Declaration declaration) => declaration switch
    {
        EntityTypeDeclaration t => [("system", t.System), ("table", t.Table), ("key", t.Key)],
        RelationDeclaration r => [("from", r.From), ("via", r.Via), ("to", r.To)],
        _ => [],
    };

    /// <summary>
    /// A declaration as one line of JSON with its members in order, so that two declarations that say the
    /// same thing read as the same text however their fields were listed.
    /// </summary>
    internal static string Canonical(Declaration declaration)
    {
        var element = JsonSerializer.SerializeToElement([declaration], OntologyJson.Default.IReadOnlyListDeclaration)[0];
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, LineOptions))
        {
            Write(writer, element);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());

        static void Write(Utf8JsonWriter writer, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        Write(writer, property.Value);
                    }

                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        Write(writer, item);
                    }

                    writer.WriteEndArray();
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }
    }

    /// <summary>A pass id from the time it ran — sortable, and the same text on every machine.</summary>
    public static string PassId(DateTimeOffset ranAt) =>
        "pass-" + ranAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
}
