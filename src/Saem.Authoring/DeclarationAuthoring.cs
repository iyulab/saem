using System.Globalization;
using System.Text;
using Eyu.Core.Declared;
using Eyu.Core.Ports;
using Eyu.Core.Primitives;
using Eyu.Core.Proposals;
using Eyu.Core.Records;
using Saem.Ontology;

namespace Saem.Authoring;

/// <summary>
/// One pass of the authoring step, from the sources to a draft: sample each subject, read what each
/// declares, ask Eyu for a proposal, and draft declarations from it. Each subject reaches Eyu declared
/// with its key, so rows of one table with different keys are never taken for one entity. The types already declared are
/// handed to Eyu as names in use, so an entity of a kind the ontology already has is proposed under
/// that name rather than a new one for the same kind — the draft then adds to the type instead of
/// declaring a second.
/// </summary>
/// <param name="proposer">Eyu's proposer — the only place a model may be called.</param>
/// <param name="records">Where each subject's records are sampled from.</param>
/// <param name="structures">Where each subject's declaration is read from; omitted, Eyu judges from records alone.</param>
public sealed class DeclarationAuthoring(IOntologyProposer proposer, IRecordSample records, IStructureSource? structures = null)
{
    /// <summary>Samples the subjects, asks for a proposal and drafts declarations from it.</summary>
    /// <param name="sources">The subjects to sample, by name, each with the source table its records are rows of.</param>
    /// <param name="existing">The declarations already in force.</param>
    /// <param name="recordsPerSubject">How many records to sample from each subject.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <exception cref="OntologyDeclarationException">The existing declarations are not consistent.</exception>
    public async Task<AuthoringPass> DraftAsync(
        IReadOnlyDictionary<string, SubjectSource> sources,
        IReadOnlyList<Declaration> existing,
        int recordsPerSubject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recordsPerSubject);

        // Refused before any record is read or any model is asked: a pass over an inconsistent
        // ontology could only end in a draft nobody can apply.
        var declared = Ontology.Ontology.Create(existing);

        var sampled = new List<RawRecord>();
        var declarations = new List<DeclaredStructure>();
        foreach (var name in sources.Keys.Order(StringComparer.Ordinal))
        {
            var subject = SubjectRef.Create(name);

            // The drafter places a type by the subject of the records that denote it, so a record
            // that came back without one is taken as the subject it was sampled from.
            var sample = await records.SampleAsync(subject, recordsPerSubject, cancellationToken).ConfigureAwait(false);
            sampled.AddRange(sample.Select(r => r.Subject is null ? r with { Subject = subject } : r));

            // The subject's key is a fact the caller knows: two rows of one table with different keys are
            // different things. Told to Eyu, its pre-filter never weighs such a pair — without it, a small
            // sample with no true duplicate can read values two rows happen to share as identity. A
            // subject with nothing else declared is declared by its name and key.
            var structure = structures is null ? null : await structures.GetStructureAsync(subject, cancellationToken).ConfigureAwait(false);
            declarations.Add((structure ?? new DeclaredStructure(subject, [], [])) with { KeyField = sources[name].KeyField });
        }

        var prior = new PriorKnowledge([], [], [.. declared.EntityTypes.Keys.Select(type => new KnownType(type))]);
        var proposal = await proposer.ProposeAsync(declarations, sampled, prior, cancellationToken).ConfigureAwait(false);

        return new AuthoringPass(proposal, sampled, DeclarationDrafter.Draft(proposal, sampled, sources, existing));
    }
}

/// <summary>
/// What one authoring pass produced: the draft to review, and what it was drawn from — the proposal,
/// with whatever of the model's answer Eyu left out (<see cref="OntologyProposal.Rejections"/>), and
/// the records every claim and note cites.
/// </summary>
/// <param name="Proposal">What Eyu proposed.</param>
/// <param name="Records">The records sampled, each with its subject.</param>
/// <param name="Draft">The declarations drafted from the proposal.</param>
public sealed record AuthoringPass(OntologyProposal Proposal, IReadOnlyList<RawRecord> Records, DeclarationDraft Draft)
{
    /// <summary>How many of a declaration's records the review shows; the rest are counted.</summary>
    public const int RecordsShownPerItem = 3;

    /// <summary>
    /// The pass as a person reviews it, in Markdown: what the draft adds, each declaration with the
    /// records it was drawn from shown as they read; what could not be declared and why; what Eyu left
    /// out of the model's answer; entities the proposal says several records denote, since that is a
    /// claim the records are one thing; and the draft itself, the file that would replace the catalog.
    /// Nothing is decided here — the reviewer accepts the draft, trims it, or declares by hand.
    /// </summary>
    public string ToReview()
    {
        var records = Records.GroupBy(r => r.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var added = Draft.Added;
        var merged = Proposal.Entities.Where(e => e.DenotedBy.Count > 1).ToList();
        var text = new StringBuilder();

        text.AppendLine("# Ontology draft for review").AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"{added.OfType<EntityTypeDeclaration>().Count()} entity types and {added.OfType<RelationDeclaration>().Count()} relations added · " +
            $"{Draft.Notes.Count} not declared · {Proposal.Rejections.Count} left out by Eyu · {Records.Count} records read.");

        Section(text, "Added", added, d => $"**{d.Name}** — {Describe(d)}", d => Draft.Evidence.GetValueOrDefault(d.Name, []));
        Section(text, "Not declared", Draft.Notes, n => $"**{n.About}** — {n.Reason}", n => n.Records);
        Section(text, "Left out by Eyu", Proposal.Rejections, r => $"**{r.Element}** {r.Id} — {r.Reason}: {r.Detail}", _ => []);
        Section(text, "Proposed as one thing by several records", merged,
            e => $"**{e.Name}** (`{e.EntityType}`) — check these records denote one entity", e => e.DenotedBy);

        text.AppendLine().AppendLine("## The draft").AppendLine();
        text.AppendLine("The catalog file as it would be written — the declarations in force first, then the added ones.").AppendLine();
        text.AppendLine("```json").Append(Draft.ToJson()).AppendLine("```");
        return text.ToString();

        void Section<T>(StringBuilder into, string title, IReadOnlyList<T> items, Func<T, string> line, Func<T, IReadOnlyList<string>> cited)
        {
            if (items.Count == 0)
            {
                return;
            }

            into.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"## {title}").AppendLine();
            foreach (var item in items)
            {
                into.AppendLine(CultureInfo.InvariantCulture, $"- {line(item)}");
                var ids = cited(item);
                foreach (var id in ids.Take(RecordsShownPerItem))
                {
                    into.AppendLine(CultureInfo.InvariantCulture, $"  - `{id}`{(records.TryGetValue(id, out var record) ? " " + Show(record) : "")}");
                }

                if (ids.Count > RecordsShownPerItem)
                {
                    into.AppendLine(CultureInfo.InvariantCulture, $"  - … and {ids.Count - RecordsShownPerItem} more");
                }
            }
        }
    }

    private static string Describe(Declaration declaration) => declaration switch
    {
        EntityTypeDeclaration t => $"on `{t.System}.{t.Table}`, keyed by `{t.Key}`; fields {string.Join(", ", t.Fields.Select(f => f.Key == f.Value ? $"`{f.Key}`" : $"`{f.Key}` (`{f.Value}`)"))}",
        RelationDeclaration r => $"from `{r.From}` through `{r.Via}` to `{r.To}`",
        _ => declaration.Name,
    };

    private static string Show(RawRecord record) =>
        $"({record.Subject?.Value ?? "no subject"}): " +
        string.Join(", ", record.Fields.Where(f => !string.IsNullOrWhiteSpace(f.Value)).Select(f => $"{f.Key} = {f.Value}"));
}
