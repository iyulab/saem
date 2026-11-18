using Saem.Ontology;

namespace Saem.Authoring.Shadow;

/// <summary>What a recheck judged, and the proposals it left alone.</summary>
/// <param name="Rows">One row for each proposal judged.</param>
/// <param name="NotJudged">The ids of proposals of a kind the check reads that the pass kept no citations for.</param>
public sealed record SourceRecheckResult(IReadOnlyList<ShadowRow> Rows, IReadOnlyList<string> NotJudged);

/// <summary>
/// Looks again, in the source, at the records each proposal was drawn from: is the cited version still the current one of its
/// record, was it corrected or retired since the sample was taken, and does it carry the field the proposal depends on.
/// No model is asked.
/// </summary>
/// <remarks>
/// What this finds is that the line from the proposal to its records still holds — not that the records support what the proposal
/// says. Straight after a pass it nearly always finds the line whole, since the proposer already refuses ids it was not given
/// and the drafter already sets aside a type whose records carry no key; it looks for what moved in the source after the
/// sample was taken, and for a citation that no longer leads anywhere.
/// </remarks>
public static class SourceRecheck
{
    /// <summary>The name this check gives itself in the ledger.</summary>
    public const string Layer = "source-recheck";

    /// <summary>The version of the rules below; a change to them is a new version.</summary>
    public const int Version = 1;

    /// <summary>Judges the entity types and relations of <paramref name="proposals"/> against the source.</summary>
    /// <param name="proposals">The pass's proposals, with the records each was drawn from.</param>
    /// <param name="source">Where the cited records are read again.</param>
    /// <param name="at">When the check runs.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public static async Task<SourceRecheckResult> JudgeAsync(
        ProposedDeclarations proposals, ICitationSource source, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(source);

        var items = new List<(Declaration Declaration, string Id, string? Field, IReadOnlyList<PassCitation> Cites)>();
        var notJudged = new List<string>();
        foreach (var declaration in proposals.Added)
        {
            string? field;
            switch (declaration)
            {
                case EntityTypeDeclaration entity:
                    field = entity.Key;
                    break;
                case RelationDeclaration relation:
                    field = relation.Via;
                    break;
                default:
                    continue;
            }

            var id = ProposedDeclarations.IdOf(declaration);
            if (proposals.Evidence?.GetValueOrDefault(id) is { Count: > 0 } cites)
            {
                items.Add((declaration, id, field, cites));
            }
            else
            {
                notJudged.Add(id);
            }
        }

        // Each subject's records are read once however many proposals cite them.
        var readings = new Dictionary<(string Subject, string Record), CitedReading>();
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var subject in items.SelectMany(i => i.Cites).Select(c => c.Subject).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            var ids = items.SelectMany(i => i.Cites).Where(c => c.Subject == subject).Select(c => c.Record).Distinct(StringComparer.Ordinal).ToList();
            try
            {
                var read = await source.ReadAsync(subject, ids, cancellationToken).ConfigureAwait(false);
                foreach (var id in ids)
                {
                    readings[(subject, id)] = read.GetValueOrDefault(id) ?? new CitedReading(CitedStanding.Missing, NoFields);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                notes[subject] = $"The source of '{subject}' could not be read: {e.Message}";
                foreach (var id in ids)
                {
                    readings[(subject, id)] = new CitedReading(CitedStanding.Unreadable, NoFields);
                }
            }
        }

        var rows = new List<ShadowRow>();
        foreach (var (declaration, id, field, cites) in items)
        {
            var found = cites
                .Select(c => (Cite: c, Reading: c.Subject is null ? new CitedReading(CitedStanding.NotCheckable, NoFields) : readings[(c.Subject, c.Record)]))
                .ToList();
            var readable = found.Where(f => f.Reading.Standing is CitedStanding.Standing or CitedStanding.Superseded).ToList();
            bool? carried = readable.Count == 0 ? null : readable.Any(f => f.Reading.Fields.TryGetValue(field!, out var value) && !string.IsNullOrWhiteSpace(value));

            string verdict;
            if (found.Any(f => f.Reading.Standing is CitedStanding.Missing or CitedStanding.Retired) || carried == false)
            {
                verdict = ShadowVerdict.Unsupported;
            }
            else if (found.Any(f => f.Reading.Standing is CitedStanding.Unreadable or CitedStanding.NotCheckable))
            {
                verdict = ShadowVerdict.Unreadable;
            }
            else
            {
                verdict = ShadowVerdict.Supported;
            }

            var note = string.Join(" ", cites.Select(c => c.Subject).OfType<string>().Distinct(StringComparer.Ordinal).Select(s => notes.GetValueOrDefault(s)).OfType<string>());
            rows.Add(new ShadowRow(
                proposals.Pass,
                id,
                ProposedDeclarations.KindOf(declaration),
                declaration.Name,
                Layer,
                Version,
                verdict,
                field,
                carried,
                [.. found.Select(f => new ShadowCite(f.Cite.Record, f.Cite.Subject, f.Reading.Standing))],
                note.Length == 0 ? null : note,
                at));
        }

        return new SourceRecheckResult(rows, notJudged);
    }

    private static readonly IReadOnlyDictionary<string, string?> NoFields = new Dictionary<string, string?>();
}
