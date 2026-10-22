using Eyu.Core.Declared;
using Eyu.Core.Ports;
using Eyu.Core.Primitives;
using Eyu.Core.Proposals;
using Eyu.Core.Records;
using Saem.Ontology;

namespace Saem.Authoring;

/// <summary>
/// One pass of the authoring step, from the sources to a draft: sample each subject, read what each
/// declares, ask Eyu for a proposal, and draft declarations from it. The types already declared are
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

            if (structures is not null && await structures.GetStructureAsync(subject, cancellationToken).ConfigureAwait(false) is { } structure)
            {
                declarations.Add(structure);
            }
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
public sealed record AuthoringPass(OntologyProposal Proposal, IReadOnlyList<RawRecord> Records, DeclarationDraft Draft);
