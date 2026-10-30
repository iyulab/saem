using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Saem.Ontology;

/// <summary>
/// What one authoring pass was made with: the model asked, the proposer that wrote the prompt, and the
/// fixed wording of that prompt, so two drafts can be read as drawn from the same instructions or not.
/// Kept beside the pass's proposals (<see cref="ProposedDeclarations"/>), and shown in its review.
/// </summary>
/// <param name="Model">The model the pass asked.</param>
/// <param name="ProposerVersion">The version of the proposer library that built the prompts.</param>
/// <param name="PromptFingerprint">The fingerprint of the prompt's fixed wording; the same wording yields the same fingerprint on any input.</param>
/// <param name="KnownTypesPromptFingerprint">The fingerprint of the wording added when the catalog in force already declares types the pass names in use; null when it declared none.</param>
/// <param name="StartedAt">When the pass started.</param>
/// <param name="ModelCallsRetried">How many model call attempts failed transiently and were tried again.</param>
public sealed record PassGeneration(
    string Model,
    string ProposerVersion,
    string PromptFingerprint,
    string? KnownTypesPromptFingerprint,
    DateTimeOffset StartedAt,
    int ModelCallsRetried)
{
    /// <summary>The generation as a section of the review, saying what the draft was made with.</summary>
    public string ToReviewSection()
    {
        var text = new StringBuilder();
        text.AppendLine().AppendLine("## Generated with").AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- model: {Model}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- proposer: {ProposerVersion}");
        var known = KnownTypesPromptFingerprint is { } fingerprint ? $" + known types {fingerprint}" : "";
        text.AppendLine(CultureInfo.InvariantCulture, $"- prompt: {PromptFingerprint}{known}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- started: {StartedAt.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- model calls retried: {ModelCallsRetried}");
        return text.ToString();
    }

    internal void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("model", Model);
        writer.WriteString("proposerVersion", ProposerVersion);
        writer.WriteString("promptFingerprint", PromptFingerprint);
        if (KnownTypesPromptFingerprint is not null)
        {
            writer.WriteString("knownTypesPromptFingerprint", KnownTypesPromptFingerprint);
        }

        writer.WriteString("startedAt", StartedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteNumber("modelCallsRetried", ModelCallsRetried);
        writer.WriteEndObject();
    }

    /// <exception cref="JsonException">The element is not what <see cref="Write"/> wrote.</exception>
    internal static PassGeneration Read(JsonElement element)
    {
        const string Shape = "A pass's \"generation\" is an object with \"model\", \"proposerVersion\", \"promptFingerprint\", \"startedAt\" and \"modelCallsRetried\".";
        if (element.ValueKind != JsonValueKind.Object
            || Text(element, "model") is not { } model
            || Text(element, "proposerVersion") is not { } version
            || Text(element, "promptFingerprint") is not { } fingerprint
            || !DateTimeOffset.TryParse(Text(element, "startedAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var startedAt)
            || !element.TryGetProperty("modelCallsRetried", out var retried) || !retried.TryGetInt32(out var retries) || retries < 0)
        {
            throw new JsonException(Shape);
        }

        string? known = null;
        if (element.TryGetProperty("knownTypesPromptFingerprint", out var knownElement) && knownElement.ValueKind != JsonValueKind.Null)
        {
            known = knownElement.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(knownElement.GetString())
                ? knownElement.GetString()
                : throw new JsonException(Shape);
        }

        return new PassGeneration(model, version, fingerprint, known, startedAt, retries);

        static string? Text(JsonElement owner, string name) =>
            owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;
    }
}
