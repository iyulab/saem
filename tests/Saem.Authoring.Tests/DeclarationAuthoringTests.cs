using Eyu.Core.Declared;
using Eyu.Core.Grounding;
using Eyu.Core.Ports;
using Eyu.Core.Primitives;
using Eyu.Core.Proposals;
using Eyu.Core.Records;
using Saem.Ontology;
using Xunit;

namespace Saem.Authoring.Tests;

/// <summary>
/// One pass from the sources to a draft: what reaches Eyu (every subject's records, each with its
/// subject; the subjects' declarations; the names the ontology already uses) and what comes back.
/// </summary>
public sealed class DeclarationAuthoringTests
{
    private static readonly Dictionary<string, SubjectSource> Sources = new()
    {
        ["work_orders"] = new("mes", "work_orders", "wo_no"),
        ["items"] = new("erp", "visible_items", "item_code"),
    };

    private static readonly Dictionary<string, IReadOnlyList<RawRecord>> Rows = new()
    {
        // A sampler that does not stamp the subject — the pass stamps it.
        ["work_orders"] = [new("wo-1", new Dictionary<string, string?> { ["wo_no"] = "WO-1", ["item_code"] = "I-1" })],
        ["items"] = [new("item-1", new Dictionary<string, string?> { ["item_code"] = "I-1", ["item_name"] = "Bolt" }, SubjectRef.Create("items"))],
    };

    private static readonly IReadOnlyList<Declaration> Existing =
    [
        new EntityTypeDeclaration("Item", "erp", "visible_items", "item_code", new Dictionary<string, string> { ["name"] = "item_name" }),
    ];

    [Fact]
    public async Task Eyu_is_given_every_subjects_records_with_their_subject_and_the_type_names_in_use()
    {
        var proposer = new RecordingProposer(Rig);
        var pass = await new DeclarationAuthoring(proposer, new Sampler()).DraftAsync(Sources, Existing, 5, TestContext.Current.CancellationToken);

        Assert.Equal(["item-1", "wo-1"], proposer.Records!.Select(r => r.Id));
        Assert.Equal(["items", "work_orders"], proposer.Records!.Select(r => r.Subject?.Value));
        Assert.Equal(["Item"], proposer.Prior!.KnownTypes.Select(t => t.Name));
        Assert.Empty(proposer.Declared!);
        Assert.Equal(pass.Records, proposer.Records);
    }

    [Fact]
    public async Task The_draft_adds_to_what_is_declared()
    {
        var pass = await new DeclarationAuthoring(new RecordingProposer(Rig), new Sampler()).DraftAsync(Sources, Existing, 5, TestContext.Current.CancellationToken);

        Assert.Equal(["WorkOrder", "orders_item"], pass.Draft.Added.Select(d => d.Name));
        Assert.Equal(new RelationDeclaration("orders_item", "WorkOrder", "item_code", "Item"), pass.Draft.Added[1]);
        Assert.Equal(Existing[0], pass.Draft.Declarations[0]);
        Assert.Empty(pass.Draft.Notes);
    }

    [Fact]
    public async Task A_subjects_declaration_reaches_eyu_when_there_is_one()
    {
        var proposer = new RecordingProposer(Rig);
        var structures = new Structures(new DeclaredStructure(SubjectRef.Create("items"), [new DeclaredField("item_name")], [], "1"));

        await new DeclarationAuthoring(proposer, new Sampler(), structures).DraftAsync(Sources, Existing, 5, TestContext.Current.CancellationToken);

        var declared = Assert.Single(proposer.Declared!);
        Assert.Equal("items", declared.Subject.Value);
    }

    [Fact]
    public async Task An_inconsistent_ontology_is_refused_before_any_record_is_read()
    {
        var sampler = new Sampler();
        IReadOnlyList<Declaration> broken = [new RelationDeclaration("orders_item", "WorkOrder", "item", "Item")];

        await Assert.ThrowsAsync<OntologyDeclarationException>(() =>
            new DeclarationAuthoring(new RecordingProposer(Rig), sampler).DraftAsync(Sources, broken, 5, TestContext.Current.CancellationToken));
        Assert.Equal(0, sampler.Calls);
    }

    private static OntologyProposal Rig(IReadOnlyList<RawRecord> records)
    {
        var wo = EntityProposal.Create("e1", "WO-1", "WorkOrder", GroundedClaim.Create("work order", [new SourceRef("wo-1")]), VocabularyOrigin.Acquired, 0.9);
        var item = EntityProposal.Create("e2", "Bolt", "Item", GroundedClaim.Create("item", [new SourceRef("item-1"), new SourceRef("wo-1")]), VocabularyOrigin.Acquired, 0.9, denotedBy: ["item-1"]);
        var orders = RelationProposal.Create("orders_item", "e1", "e2", GroundedClaim.Create("orders", [new SourceRef("wo-1"), new SourceRef("item-1")]), VocabularyOrigin.Acquired, 0.9);
        return new OntologyProposal([wo, item], [orders], []);
    }

    private sealed class RecordingProposer(Func<IReadOnlyList<RawRecord>, OntologyProposal> answer) : IOntologyProposer
    {
        public IReadOnlyList<DeclaredStructure>? Declared { get; private set; }

        public IReadOnlyList<RawRecord>? Records { get; private set; }

        public PriorKnowledge? Prior { get; private set; }

        public Task<OntologyProposal> ProposeAsync(IReadOnlyList<DeclaredStructure> declaredStructures, IReadOnlyList<RawRecord> records, PriorKnowledge prior, CancellationToken cancellationToken = default)
        {
            (Declared, Records, Prior) = (declaredStructures, records, prior);
            return Task.FromResult(answer(records));
        }
    }

    private sealed class Sampler : IRecordSample
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<RawRecord>> SampleAsync(SubjectRef subject, int maxCount, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<RawRecord>>([.. Rows[subject.Value].Take(maxCount)]);
        }
    }

    private sealed class Structures(params DeclaredStructure[] declared) : IStructureSource
    {
        public Task<DeclaredStructure?> GetStructureAsync(SubjectRef subject, CancellationToken cancellationToken = default) =>
            Task.FromResult(declared.FirstOrDefault(d => d.Subject == subject));
    }
}
