using Saem.Authoring.Shadow;
using Saem.Ontology;
using Xunit;

namespace Saem.Authoring.Tests;

/// <summary>
/// The recheck of a pass's proposals against the source: what each cited record turned into since the sample was taken, and
/// whether the proposal's line to its records still holds. No model is asked; the source is a stand-in that says how each
/// record stands.
/// </summary>
public sealed class SourceRecheckTests : IDisposable
{
    private static readonly EntityTypeDeclaration WorkOrder = new("WorkOrder", "mes", "work_orders", "wo", new Dictionary<string, string>());
    private static readonly EntityTypeDeclaration Item = new("Item", "erp", "items", "item_code", new Dictionary<string, string>());
    private static readonly RelationDeclaration MadeOf = new("madeOf", "WorkOrder", "item", "Item");
    private static readonly DateTimeOffset At = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Directory.CreateTempSubdirectory("saem-shadow-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ProposedDeclarations Proposed(params (string Id, string Record, string? Subject)[] cites) =>
        new("pass-1", [WorkOrder, Item, MadeOf], null,
            cites.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => (IReadOnlyList<PassCitation>)[.. g.Select(c => new PassCitation(c.Record, c.Subject))]));

    private static CitedReading Reading(CitedStanding standing, params (string Field, string? Value)[] fields) =>
        new(standing, fields.ToDictionary(f => f.Field, f => f.Value));

    [Fact]
    public async Task Records_that_still_stand_and_carry_the_key_support_the_proposal()
    {
        var source = new FakeSource { ["workorders"] = new() { ["d-1"] = Reading(CitedStanding.Standing, ("wo", "WO-1")) } };

        var result = await SourceRecheck.JudgeAsync(Proposed(("entityType:WorkOrder", "d-1", "workorders")), source, At, TestContext.Current.CancellationToken);

        var row = Assert.Single(result.Rows);
        Assert.Equal((ShadowVerdict.Supported, "wo", true), (row.Verdict, row.Field, row.FieldCarried));
        Assert.Equal(("entityType:WorkOrder", "entityType", "WorkOrder", SourceRecheck.Layer, SourceRecheck.Version), (row.ItemId, row.Kind, row.Name, row.Layer, row.LayerVersion));
        Assert.Equal([new ShadowCite("d-1", "workorders", CitedStanding.Standing)], row.Cites);
    }

    [Fact]
    public async Task A_record_corrected_since_is_counted_apart_and_does_not_by_itself_unsupport_the_proposal()
    {
        var source = new FakeSource { ["workorders"] = new() { ["d-1"] = Reading(CitedStanding.Superseded, ("wo", "WO-1")) } };

        var row = Assert.Single((await SourceRecheck.JudgeAsync(Proposed(("entityType:WorkOrder", "d-1", "workorders")), source, At, TestContext.Current.CancellationToken)).Rows);

        Assert.Equal(ShadowVerdict.Supported, row.Verdict);
        Assert.Equal(CitedStanding.Superseded, Assert.Single(row.Cites).Standing);
    }

    [Theory]
    [InlineData(CitedStanding.Missing)]
    [InlineData(CitedStanding.Retired)]
    public async Task A_record_that_is_gone_or_retired_leaves_the_proposal_unsupported(CitedStanding standing)
    {
        var source = new FakeSource
        {
            ["workorders"] = new() { ["d-1"] = Reading(CitedStanding.Standing, ("wo", "WO-1")), ["d-2"] = Reading(standing) },
        };

        var row = Assert.Single((await SourceRecheck.JudgeAsync(Proposed(("entityType:WorkOrder", "d-1", "workorders"), ("entityType:WorkOrder", "d-2", "workorders")), source, At, TestContext.Current.CancellationToken)).Rows);

        Assert.Equal(ShadowVerdict.Unsupported, row.Verdict);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Records_that_none_of_them_carry_the_field_the_proposal_depends_on_leave_it_unsupported(string? blank)
    {
        var source = new FakeSource { ["workorders"] = new() { ["d-1"] = Reading(CitedStanding.Standing, ("wo", blank), ("other", "x")) } };

        var row = Assert.Single((await SourceRecheck.JudgeAsync(Proposed(("entityType:WorkOrder", "d-1", "workorders")), source, At, TestContext.Current.CancellationToken)).Rows);

        Assert.Equal((ShadowVerdict.Unsupported, false), (row.Verdict, row.FieldCarried));
    }

    [Fact]
    public async Task A_relation_depends_on_the_field_that_carries_it()
    {
        var source = new FakeSource
        {
            ["workorders"] = new() { ["d-1"] = Reading(CitedStanding.Standing, ("wo", "WO-1"), ("item", "I-100")) },
        };

        var row = Assert.Single((await SourceRecheck.JudgeAsync(
            new ProposedDeclarations("pass-1", [MadeOf], null, new Dictionary<string, IReadOnlyList<PassCitation>> { ["relation:madeOf"] = [new PassCitation("d-1", "workorders")] }),
            source, At, TestContext.Current.CancellationToken)).Rows);

        Assert.Equal((ShadowVerdict.Supported, "item", true), (row.Verdict, row.Field, row.FieldCarried));
    }

    [Fact]
    public async Task A_source_that_cannot_be_read_leaves_the_proposal_unreadable_and_says_why_without_stopping_the_others()
    {
        var source = new FakeSource
        {
            ["workorders"] = new() { ["d-1"] = Reading(CitedStanding.Standing, ("wo", "WO-1")) },
            ["items"] = null,
        };

        var result = await SourceRecheck.JudgeAsync(Proposed(("entityType:WorkOrder", "d-1", "workorders"), ("entityType:Item", "d-9", "items")), source, At, TestContext.Current.CancellationToken);

        Assert.Equal(ShadowVerdict.Supported, result.Rows.Single(r => r.Name == "WorkOrder").Verdict);
        var unread = result.Rows.Single(r => r.Name == "Item");
        Assert.Equal(ShadowVerdict.Unreadable, unread.Verdict);
        Assert.Contains("items", unread.Note);
        Assert.Equal(CitedStanding.Unreadable, Assert.Single(unread.Cites).Standing);
    }

    [Fact]
    public async Task A_citation_that_does_not_say_what_it_is_of_cannot_be_looked_up_again()
    {
        var row = Assert.Single((await SourceRecheck.JudgeAsync(Proposed(("entityType:WorkOrder", "d-1", null)), new FakeSource(), At, TestContext.Current.CancellationToken)).Rows);

        Assert.Equal((ShadowVerdict.Unreadable, CitedStanding.NotCheckable), (row.Verdict, Assert.Single(row.Cites).Standing));
    }

    [Fact]
    public async Task A_proposal_the_pass_kept_no_citations_for_is_left_alone_and_a_kind_the_check_does_not_read_is_not_counted()
    {
        var proposals = new ProposedDeclarations("pass-1", [WorkOrder, Item], null,
            new Dictionary<string, IReadOnlyList<PassCitation>> { ["entityType:WorkOrder"] = [new PassCitation("d-1", "workorders")] });
        var source = new FakeSource { ["workorders"] = new() { ["d-1"] = Reading(CitedStanding.Standing, ("wo", "WO-1")) } };

        var result = await SourceRecheck.JudgeAsync(proposals, source, At, TestContext.Current.CancellationToken);

        Assert.Equal(["entityType:WorkOrder"], result.Rows.Select(r => r.ItemId));
        Assert.Equal(["entityType:Item"], result.NotJudged);
    }

    [Fact]
    public async Task A_subject_is_read_once_however_many_proposals_cite_it()
    {
        var source = new FakeSource
        {
            ["workorders"] = new()
            {
                ["d-1"] = Reading(CitedStanding.Standing, ("wo", "WO-1"), ("item", "I-1")),
                ["d-2"] = Reading(CitedStanding.Standing, ("wo", "WO-2"), ("item", "I-1")),
            },
        };

        await SourceRecheck.JudgeAsync(Proposed(("entityType:WorkOrder", "d-1", "workorders"), ("relation:madeOf", "d-2", "workorders")), source, At, TestContext.Current.CancellationToken);

        Assert.Equal(1, source.Reads["workorders"]);
    }

    [Fact]
    public async Task What_a_check_found_is_appended_and_read_back_and_each_run_adds_to_it()
    {
        var source = new FakeSource { ["workorders"] = new() { ["d-1"] = Reading(CitedStanding.Superseded, ("wo", "WO-1")) } };
        var rows = (await SourceRecheck.JudgeAsync(Proposed(("entityType:WorkOrder", "d-1", "workorders")), source, At, TestContext.Current.CancellationToken)).Rows;
        var path = Path.Combine(_dir, "shadow-judgments.jsonl");

        Assert.Empty(await ShadowLedger.ReadAsync(path, TestContext.Current.CancellationToken));
        await ShadowLedger.AppendAsync(path, rows, TestContext.Current.CancellationToken);
        await ShadowLedger.AppendAsync(path, rows, TestContext.Current.CancellationToken);

        var read = await ShadowLedger.ReadAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(2, read.Count);
        Assert.All(read, r => Assert.Equal(rows[0], r, new RowComparer()));
        Assert.Equal(2, (await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken)).Length);
    }

    [Theory]
    [InlineData("""{"v":2,"pass":"p"}""")]
    [InlineData("""{"v":1,"pass":"p"}""")]
    public void A_line_that_is_not_a_row_of_this_layout_is_refused(string line) =>
        Assert.Throws<System.Text.Json.JsonException>(() => ShadowLedger.Parse(line));

    /// <summary>Rows compared by what they say; the cites list makes the generated equality compare by reference.</summary>
    private sealed class RowComparer : IEqualityComparer<ShadowRow>
    {
        public bool Equals(ShadowRow? x, ShadowRow? y) => x is not null && y is not null && ShadowLedger.Write(x) == ShadowLedger.Write(y);

        public int GetHashCode(ShadowRow obj) => ShadowLedger.Write(obj).GetHashCode(StringComparison.Ordinal);
    }

    /// <summary>A source keyed by subject: how each record stands now. A subject mapped to null cannot be read.</summary>
    private sealed class FakeSource : Dictionary<string, Dictionary<string, CitedReading>?>, ICitationSource
    {
        public Dictionary<string, int> Reads { get; } = [];

        public Task<IReadOnlyDictionary<string, CitedReading>> ReadAsync(string subject, IReadOnlyCollection<string> recordIds, CancellationToken cancellationToken)
        {
            Reads[subject] = Reads.GetValueOrDefault(subject) + 1;
            if (!TryGetValue(subject, out var records) || records is null)
            {
                throw new InvalidOperationException("the stand-in source is down");
            }

            return Task.FromResult<IReadOnlyDictionary<string, CitedReading>>(recordIds.ToDictionary(id => id, id => records.GetValueOrDefault(id) ?? new CitedReading(CitedStanding.Missing, new Dictionary<string, string?>())));
        }
    }
}
