using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eyu.Core.Inference;
using Eyu.Core.Ports;
using Saem.Ontology;

namespace Saem.Authoring.Shadow;

/// <summary>
/// Asks the same model the same question about each proposal several times, one repeat after another, and records what each repeat
/// said: a proposal whose repeats all agree is stable, one whose repeats differ is wobbling. This measures whether the model says the
/// same thing twice — not whether it is right: one model can be steadily wrong.
/// </summary>
/// <remarks>
/// The model is asked with the records the proposal cites as the source holds them now, and nothing else. How it samples is the
/// endpoint's own: the request carries no setting for it.
/// </remarks>
public static class RepeatConsistency
{
    /// <summary>The name this check gives itself in the ledger.</summary>
    public const string Layer = "repeat-consistency";

    /// <summary>The version of the rules below; a change to them — the wording included — is a new version.</summary>
    public const int Version = 1;

    /// <summary>How many times each proposal is asked about.</summary>
    public const int DefaultRepeats = 5;

    /// <summary>The most proposals one run asks about; a pass with more is stopped before any call is made.</summary>
    public const int MaxItems = 10;

    private const int MaxRecords = 5;
    private const int MaxFields = 30;
    private const int MaxValueLength = 200;

    private static readonly string[] Verdicts = ["supported", "unsupported", "unsure"];

    /// <summary>A fingerprint of the wording the model is asked with.</summary>
    public static string PromptFingerprint { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Wording))).ToLowerInvariant()[..8];

    private const string Wording = """
        You check one proposal against the records it was drawn from, and against nothing else.
        A proposal is a statement about how a source system is organised. Say whether the records, as they are written below, support it.
        Answer "supported" when the records show what the proposal says, "unsupported" when they contradict it or show something else,
        and "unsure" when they do not settle it.
        Reply with JSON only, no other text: {"verdict": "supported" | "unsupported" | "unsure", "reason": "<one sentence>"}
        """;

    /// <summary>The proposal's own statement of what it claims, in words a reader of the records can check.</summary>
    public static string Describe(Declaration declaration) => declaration switch
    {
        EntityTypeDeclaration e =>
            $"An entity type named '{e.Name}': what the records of table '{e.Table}' in system '{e.System}' describe, each identified by its '{e.Key}' value"
            + (e.Fields.Count == 0 ? "." : $", with the fields {string.Join(", ", e.Fields.Select(f => $"'{f.Key}' (column '{f.Value}')"))}."),
        RelationDeclaration r =>
            $"A relation named '{r.Name}' from '{r.From}' to '{r.To}': the value of the field '{r.Via}' in a '{r.From}' record is the key of the '{r.To}' record it points to.",
        _ => declaration.Name,
    };

    /// <summary>The whole question put to the model for one proposal.</summary>
    public static string Prompt(Declaration declaration, IReadOnlyList<(string Record, IReadOnlyDictionary<string, string?> Fields)> records)
    {
        var text = new StringBuilder(Wording).AppendLine().AppendLine().Append("Proposal: ").AppendLine(Describe(declaration)).AppendLine().AppendLine("Records:");
        foreach (var (record, fields) in records.Take(MaxRecords))
        {
            text.Append("- record ").Append(record).Append(':');
            foreach (var (name, value) in fields.Take(MaxFields))
            {
                var shown = value is null ? "(none)" : value.Length > MaxValueLength ? value[..MaxValueLength] + "…" : value;
                text.Append(' ').Append(name).Append(" = ").Append(shown).Append(';');
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>What a repeat said: one of <c>supported</c>, <c>unsupported</c>, <c>unsure</c> — or <c>invalid</c> when the answer cannot be read as one.</summary>
    public static string ReadVote(string answer)
    {
        var from = answer.IndexOf('{', StringComparison.Ordinal);
        var to = answer.LastIndexOf('}');
        if (from < 0 || to <= from)
        {
            return "invalid";
        }

        try
        {
            using var document = JsonDocument.Parse(answer[from..(to + 1)]);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("verdict", out var verdict)
                && verdict.ValueKind == JsonValueKind.String
                && verdict.GetString() is { } said && Verdicts.Contains(said, StringComparer.Ordinal)
                ? said
                : "invalid";
        }
        catch (JsonException)
        {
            return "invalid";
        }
    }

    /// <summary>The proposal's verdict from what its repeats said.</summary>
    public static string VerdictOf(IReadOnlyList<string> votes) =>
        votes.Any(v => v == "invalid") ? ShadowVerdict.Incomplete
        : votes.Distinct(StringComparer.Ordinal).Count() == 1 ? ShadowVerdict.StablePrefix + votes[0]
        : ShadowVerdict.Wobbling;

    /// <summary>Asks the model about each entity type and relation of <paramref name="proposals"/>.</summary>
    /// <param name="proposals">The pass's proposals, with the records each was drawn from.</param>
    /// <param name="source">Where the cited records are read again, to put in front of the model as they stand now.</param>
    /// <param name="model">The model to ask.</param>
    /// <param name="repeats">How many times each proposal is asked about.</param>
    /// <param name="at">When the run starts.</param>
    /// <param name="progress">Told after each repeat; a model call takes minutes.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <exception cref="ArgumentException">The pass has more proposals than <see cref="MaxItems"/>.</exception>
    public static async Task<SourceRecheckResult> JudgeAsync(
        ProposedDeclarations proposals,
        ICitationSource source,
        IModelClient model,
        int repeats,
        DateTimeOffset at,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentOutOfRangeException.ThrowIfLessThan(repeats, 2);

        var (items, notJudged) = SourceRecheck.ItemsOf(proposals);
        if (items.Count > MaxItems)
        {
            throw new ArgumentException($"The pass has {items.Count} proposals to ask about and a run asks about at most {MaxItems}; asking about more is a new decision.", nameof(proposals));
        }

        var (readings, notes) = await SourceRecheck.ReadAllAsync(items.SelectMany(i => i.Cites), source, cancellationToken).ConfigureAwait(false);

        var rows = new List<ShadowRow>();
        foreach (var (declaration, id, field, cites) in items)
        {
            var found = cites
                .Select(c => (Cite: c, Reading: c.Subject is null ? new CitedReading(CitedStanding.NotCheckable, SourceRecheck.NoFields) : readings[(c.Subject, c.Record)]))
                .ToList();
            var shown = found
                .Where(f => f.Reading.Standing is CitedStanding.Standing or CitedStanding.Superseded)
                .Select(f => (f.Cite.Record, f.Reading.Fields))
                .ToList();
            var cited = found.Select(f => new ShadowCite(f.Cite.Record, f.Cite.Subject, f.Reading.Standing)).ToList();
            var note = string.Join(" ", cites.Select(c => c.Subject).OfType<string>().Distinct(StringComparer.Ordinal).Select(s => notes.GetValueOrDefault(s)).OfType<string>());

            if (shown.Count == 0)
            {
                rows.Add(new ShadowRow(proposals.Pass, id, ProposedDeclarations.KindOf(declaration), declaration.Name, Layer, Version, ShadowVerdict.Incomplete,
                    field, null, cited, note.Length > 0 ? note : "None of the cited records could be read, so there was nothing to put in front of the model.", at, [], PromptFingerprint));
                continue;
            }

            var prompt = Prompt(declaration, shown);
            var votes = new List<string>();
            var failures = new List<string>();
            for (var repeat = 1; repeat <= repeats; repeat++)
            {
                string vote;
                try
                {
                    vote = ReadVote((await model.CompleteAsync(new ModelRequest(prompt), cancellationToken).ConfigureAwait(false)).Text);
                }
                catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    vote = "invalid";
                    failures.Add($"repeat {repeat}: {e.GetType().Name}: {e.Message}");
                }

                votes.Add(vote);
                progress?.Invoke($"{id} repeat {repeat}/{repeats}: {vote}");
            }

            var extra = failures.Count == 0 ? null : string.Join(" ", failures);
            rows.Add(new ShadowRow(proposals.Pass, id, ProposedDeclarations.KindOf(declaration), declaration.Name, Layer, Version, VerdictOf(votes),
                field, null, cited, string.Join(" ", new[] { note, extra }.Where(n => !string.IsNullOrEmpty(n))) is { Length: > 0 } joined ? joined : null, at, votes, PromptFingerprint));
        }

        return new SourceRecheckResult(rows, notJudged);
    }
}
