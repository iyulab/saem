using System.Text.Json;
using Saem.Ontology;
using Xunit;

namespace Saem.Authoring.Tests;

/// <summary>
/// A pass's proposals read against the catalog a review left in force, as settled records in the form Gil
/// reads: one line per proposal, taken or not, kept as proposed or changed.
/// </summary>
public sealed class ReviewSettlementTests
{
    private static readonly EntityTypeDeclaration WorkOrder = new("WorkOrder", "mes", "work_orders", "wo_no",
        new Dictionary<string, string> { ["quantity"] = "qty", ["item"] = "item_code" });

    private static readonly EntityTypeDeclaration Item = new("Item", "erp", "items", "item_code",
        new Dictionary<string, string> { ["name"] = "item_name" });

    private static readonly RelationDeclaration MadeOf = new("madeOf", "WorkOrder", "item", "Item");

    private static readonly ProposedDeclarations Proposed = new("pass-20261025T010203000Z", [WorkOrder, Item, MadeOf]);

    private static readonly DateTimeOffset SettledAt = new(2026, 10, 25, 1, 30, 0, TimeSpan.Zero);

    private static Dictionary<string, JsonElement> Settle(IReadOnlyList<Declaration> inForce, string? settledBy = null) =>
        ReviewSettlement.Lines(Proposed, inForce, SettledAt, settledBy)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToDictionary(r => r.GetProperty("id").GetString()!);

    [Fact]
    public void A_proposal_kept_as_it_was_is_accepted_and_one_changed_is_corrected()
    {
        // The reviewer kept WorkOrder — its fields listed the other way round, which says the same thing —
        // and renamed Item's field.
        var keptAsProposed = WorkOrder with { Fields = new Dictionary<string, string> { ["item"] = "item_code", ["quantity"] = "qty" } };
        var records = Settle([keptAsProposed, Item with { Fields = new Dictionary<string, string> { ["title"] = "item_name" } }, MadeOf]);

        var workOrder = records["entityType:WorkOrder"];
        Assert.Equal("yes", workOrder.GetProperty("values").GetProperty("taken").GetString());
        Assert.Equal("AcceptOther", workOrder.GetProperty("settlements").GetProperty("taken").GetString());
        Assert.Equal("AcceptOther", workOrder.GetProperty("settlements").GetProperty("declaration").GetString());
        Assert.Equal(
            workOrder.GetProperty("suggested").GetProperty("declaration").GetString(),
            workOrder.GetProperty("values").GetProperty("declaration").GetString());

        var item = records["entityType:Item"];
        Assert.Equal("CorrectOther", item.GetProperty("settlements").GetProperty("declaration").GetString());
        Assert.Contains("\"title\"", item.GetProperty("values").GetProperty("declaration").GetString(), StringComparison.Ordinal);
        Assert.Contains("\"name\"", item.GetProperty("suggested").GetProperty("declaration").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_proposal_the_review_left_out_is_not_taken()
    {
        var records = Settle([WorkOrder, Item]);

        var madeOf = records["relation:madeOf"];
        Assert.Equal("no", madeOf.GetProperty("values").GetProperty("taken").GetString());
        Assert.Equal("CorrectOther", madeOf.GetProperty("settlements").GetProperty("taken").GetString());
        Assert.Equal("yes", madeOf.GetProperty("suggested").GetProperty("taken").GetString());
        Assert.False(madeOf.GetProperty("values").TryGetProperty("declaration", out _), "nothing was declared");
        Assert.False(madeOf.GetProperty("settlements").TryGetProperty("declaration", out _));
        Assert.Equal(("WorkOrder", "item", "Item"), (
            madeOf.GetProperty("values").GetProperty("from").GetString(),
            madeOf.GetProperty("values").GetProperty("via").GetString(),
            madeOf.GetProperty("values").GetProperty("to").GetString()));
    }

    [Fact]
    public void Every_line_carries_the_pass_as_its_batch_and_the_time_in_ticks()
    {
        var records = Settle([WorkOrder], settledBy: "r-17");

        Assert.Equal(3, records.Count);
        foreach (var record in records.Values)
        {
            Assert.Equal(Proposed.Pass, record.GetProperty("batch").GetString());
            Assert.Equal(SettledAt.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks, record.GetProperty("settled_at").GetInt64());
            Assert.Equal("r-17", record.GetProperty("settled_by").GetProperty("taken").GetString());
        }

        Assert.Equal(("mes", "work_orders", "wo_no"), (
            records["entityType:WorkOrder"].GetProperty("values").GetProperty("system").GetString(),
            records["entityType:WorkOrder"].GetProperty("values").GetProperty("table").GetString(),
            records["entityType:WorkOrder"].GetProperty("values").GetProperty("key").GetString()));
        Assert.False(Settle([WorkOrder])["entityType:WorkOrder"].TryGetProperty("settled_by", out _), "no settler given, none kept");
    }

    [Fact]
    public void A_renamed_declaration_reads_as_one_not_taken()
    {
        var records = Settle([new EntityTypeDeclaration("Order", WorkOrder.System, WorkOrder.Table, WorkOrder.Key, WorkOrder.Fields)]);

        Assert.Equal("no", records["entityType:WorkOrder"].GetProperty("values").GetProperty("taken").GetString());
        Assert.DoesNotContain("entityType:Order", records.Keys);
    }

    [Fact]
    public void The_proposals_read_back_as_written()
    {
        var read = ProposedDeclarations.Parse(Proposed.ToJson());

        Assert.Equal(Proposed.Pass, read.Pass);
        Assert.Equal(Proposed.Added.Select(ReviewSettlement.Canonical), read.Added.Select(ReviewSettlement.Canonical));
        Assert.Throws<JsonException>(() => ProposedDeclarations.Parse("""{"added":[]}"""));
        Assert.Null(read.Generation);
    }

    [Fact]
    public void What_a_pass_was_made_with_is_kept_with_its_proposals_and_read_back()
    {
        var generation = new PassGeneration("qwen-27b", "0.23.0", "a1b2c3d4", "e5f6a7b8", new DateTimeOffset(2026, 10, 30, 3, 4, 5, TimeSpan.Zero), 2);

        var read = ProposedDeclarations.Parse((Proposed with { Generation = generation }).ToJson());

        Assert.Equal(generation, read.Generation);
        Assert.Equal(Proposed.Pass, read.Pass);
    }

    [Fact]
    public void A_pass_without_known_types_keeps_no_fingerprint_for_them()
    {
        var generation = new PassGeneration("m", "1.0.0", "a1b2c3d4", null, DateTimeOffset.UnixEpoch, 0);

        var json = (Proposed with { Generation = generation }).ToJson();

        Assert.DoesNotContain("knownTypesPromptFingerprint", json, StringComparison.Ordinal);
        Assert.Null(ProposedDeclarations.Parse(json).Generation!.KnownTypesPromptFingerprint);
    }

    [Theory]
    [InlineData("""{"model":"m"}""")]
    [InlineData("""{"model":"m","proposerVersion":"1","promptFingerprint":"f","startedAt":"not a time","modelCallsRetried":0}""")]
    [InlineData("""{"model":"m","proposerVersion":"1","promptFingerprint":"f","startedAt":"2026-10-30T00:00:00Z","modelCallsRetried":-1}""")]
    [InlineData("""{"model":" ","proposerVersion":"1","promptFingerprint":"f","startedAt":"2026-10-30T00:00:00Z","modelCallsRetried":0}""")]
    [InlineData("""[]""")]
    public void A_generation_that_is_not_what_was_written_is_refused(string generation) =>
        Assert.Throws<JsonException>(() => ProposedDeclarations.Parse($$"""{"pass":"p","added":[],"generation":{{generation}}}"""));

    [Fact]
    public void The_review_says_what_the_draft_was_made_with()
    {
        var section = new PassGeneration("qwen-27b", "0.23.0", "a1b2c3d4", "e5f6a7b8", new DateTimeOffset(2026, 10, 30, 3, 4, 5, TimeSpan.Zero), 2).ToReviewSection();

        Assert.Contains("## Generated with", section, StringComparison.Ordinal);
        Assert.Contains("- model: qwen-27b", section, StringComparison.Ordinal);
        Assert.Contains("- proposer: 0.23.0", section, StringComparison.Ordinal);
        Assert.Contains("- prompt: a1b2c3d4 + known types e5f6a7b8", section, StringComparison.Ordinal);
        Assert.Contains("- started: 2026-10-30T03:04:05Z", section, StringComparison.Ordinal);
        Assert.Contains("- model calls retried: 2", section, StringComparison.Ordinal);
    }
}
