using System.Text.Json;
using Xunit;

namespace Saem.Ontology.Tests;

/// <summary>The records a pass drew each proposal from are kept with the proposals — and a file kept before they were is still read.</summary>
public class PassEvidenceTests
{
    private static readonly EntityTypeDeclaration WorkOrder = new("WorkOrder", "mes", "work_orders", "wo", new Dictionary<string, string>());

    [Fact]
    public void The_records_each_proposal_was_drawn_from_read_back_as_written()
    {
        var evidence = new Dictionary<string, IReadOnlyList<PassCitation>>
        {
            ["entityType:WorkOrder"] = [new PassCitation("d-1", "workorders"), new PassCitation("d-2", null)],
        };
        var proposed = new ProposedDeclarations("pass-1", [WorkOrder], null, evidence);

        var read = ProposedDeclarations.Parse(proposed.ToJson());

        Assert.Equal(evidence["entityType:WorkOrder"], read.Evidence!["entityType:WorkOrder"]);
    }

    [Fact]
    public void Proposals_kept_before_the_records_were_have_none_and_no_evidence_is_written_when_there_is_none()
    {
        var proposed = new ProposedDeclarations("pass-1", [WorkOrder]);

        Assert.DoesNotContain("evidence", proposed.ToJson());
        Assert.Null(ProposedDeclarations.Parse(proposed.ToJson()).Evidence);
    }

    [Theory]
    [InlineData("""{"pass":"p","added":[],"evidence":[]}""")]
    [InlineData("""{"pass":"p","added":[],"evidence":{"entityType:A":"d-1"}}""")]
    [InlineData("""{"pass":"p","added":[],"evidence":{"entityType:A":[{"subject":"s"}]}}""")]
    public void Evidence_that_is_not_what_a_pass_writes_is_refused(string json) =>
        Assert.Throws<JsonException>(() => ProposedDeclarations.Parse(json));
}
