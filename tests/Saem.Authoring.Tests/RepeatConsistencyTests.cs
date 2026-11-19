using Eyu.Core.Inference;
using Eyu.Core.Ports;
using Saem.Authoring.Shadow;
using Saem.Ontology;
using Xunit;

namespace Saem.Authoring.Tests;

/// <summary>
/// The repeated judgment: what each repeat said, when repeats agree, when they do not, and what is not a judgment at all —
/// against a stand-in model that answers from a script, so how many times it was asked is exact.
/// </summary>
public sealed class RepeatConsistencyTests
{
    private static readonly EntityTypeDeclaration WorkOrder = new("WorkOrder", "mes", "work_orders", "wo", new Dictionary<string, string> { ["item"] = "item_code" });
    private static readonly RelationDeclaration MadeOf = new("madeOf", "WorkOrder", "item", "Item");
    private static readonly DateTimeOffset At = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static ProposedDeclarations Proposed(params Declaration[] declarations) =>
        new("pass-1", declarations, null, declarations.ToDictionary(
            ProposedDeclarations.IdOf, _ => (IReadOnlyList<PassCitation>)[new PassCitation("d-1", "workorders")]));

    private static FakeSource Source() => new();

    [Theory]
    [InlineData("""{"verdict":"supported","reason":"x"}""", "supported")]
    [InlineData("""{"verdict":"unsure"}""", "unsure")]
    [InlineData("Here you go:\n```json\n{\"verdict\": \"unsupported\", \"reason\": \"no\"}\n```", "unsupported")]
    [InlineData("""{"verdict":"maybe"}""", "invalid")]
    [InlineData("""{"verdict":3}""", "invalid")]
    [InlineData("""{"answer":"supported"}""", "invalid")]
    [InlineData("supported", "invalid")]
    [InlineData("{not json}", "invalid")]
    [InlineData("", "invalid")]
    public void A_repeat_is_read_as_the_verdict_it_names_or_as_invalid(string answer, string vote) =>
        Assert.Equal(vote, RepeatConsistency.ReadVote(answer));

    [Theory]
    [InlineData(new[] { "supported", "supported", "supported" }, "stable:supported")]
    [InlineData(new[] { "unsure", "unsure" }, "stable:unsure")]
    [InlineData(new[] { "supported", "unsupported", "supported" }, "wobbling")]
    [InlineData(new[] { "supported", "supported", "invalid" }, "incomplete")]
    [InlineData(new[] { "supported", "unsupported", "invalid" }, "incomplete")]
    public void Repeats_that_all_agree_are_stable_and_any_that_fail_leave_the_proposal_incomplete(string[] votes, string verdict) =>
        Assert.Equal(verdict, RepeatConsistency.VerdictOf(votes));

    [Fact]
    public async Task Each_proposal_is_asked_about_the_requested_number_of_times_with_the_records_as_they_stand()
    {
        var model = new ScriptedModel(_ => """{"verdict":"supported","reason":"ok"}""");
        var progress = new List<string>();

        var result = await RepeatConsistency.JudgeAsync(Proposed(WorkOrder, MadeOf), Source(), model, 5, At, progress.Add, TestContext.Current.CancellationToken);

        Assert.Equal(10, model.Prompts.Count);
        Assert.Equal(10, progress.Count);
        Assert.All(result.Rows, r =>
        {
            Assert.Equal((RepeatConsistency.Layer, RepeatConsistency.Version, "stable:supported", RepeatConsistency.PromptFingerprint), (r.Layer, r.LayerVersion, r.Verdict, r.PromptFingerprint));
            Assert.Equal(["supported", "supported", "supported", "supported", "supported"], r.Votes);
        });
        Assert.All(model.Prompts, p =>
        {
            Assert.Contains("wo = WO-1", p, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", p, StringComparison.Ordinal);
        });
        Assert.Contains("An entity type named 'WorkOrder'", model.Prompts[0], StringComparison.Ordinal);
        Assert.Contains("A relation named 'madeOf'", model.Prompts[5], StringComparison.Ordinal);
    }

    [Fact]
    public void A_relation_is_asked_with_the_records_of_the_type_it_points_to_and_an_entity_type_is_not()
    {
        IReadOnlyDictionary<string, string?> order = new Dictionary<string, string?> { ["wo"] = "WO-1", ["item"] = "I-100" };
        IReadOnlyDictionary<string, string?> other = new Dictionary<string, string?> { ["code"] = "I-900", ["name"] = "Washer" };
        IReadOnlyDictionary<string, string?> pointed = new Dictionary<string, string?> { ["code"] = "I-100", ["name"] = "Bolt" };
        var item = new EntityTypeDeclaration("Item", "mes", "items", "code", new Dictionary<string, string> { ["name"] = "name" });

        var targets = RepeatConsistency.TargetsOf(MadeOf, [("d-1", order)], [("d-8", other), ("d-9", pointed)], item.Key);
        var relation = RepeatConsistency.Prompt(MadeOf, [("d-1", order)], targets);

        Assert.Equal(["d-9", "d-8"], targets.Select(t => t.Record));
        Assert.Contains("Records of the type the relation points to ('Item'):", relation, StringComparison.Ordinal);
        Assert.Contains("- record d-9: code = I-100; name = Bolt;", relation, StringComparison.Ordinal);
        Assert.Contains("(none of its records could be read)", RepeatConsistency.Prompt(MadeOf, [("d-1", order)], []), StringComparison.Ordinal);
        Assert.DoesNotContain("points to", RepeatConsistency.Prompt(WorkOrder, [("d-1", order)], targets), StringComparison.Ordinal);
    }

    [Fact]
    public void The_two_versions_of_the_prompt_keep_the_fingerprints_the_ledger_already_holds()
    {
        Assert.Equal("d18569a4", RepeatConsistency.FingerprintOf(1));
        Assert.Equal("e2438c40", RepeatConsistency.FingerprintOf(2));
        Assert.Equal(RepeatConsistency.FingerprintOf(RepeatConsistency.Version), RepeatConsistency.PromptFingerprint);
    }

    [Fact]
    public async Task Version_one_asks_a_relation_without_the_target_records_and_says_so_in_the_ledger_and_an_entity_prompt_is_the_same_in_both()
    {
        var item = new EntityTypeDeclaration("Item", "mes", "items", "code", new Dictionary<string, string>());
        var proposals = new ProposedDeclarations("pass-1", [WorkOrder, item, MadeOf], null, new Dictionary<string, IReadOnlyList<PassCitation>>
        {
            [ProposedDeclarations.IdOf(WorkOrder)] = [new PassCitation("d-1", "workorders")],
            [ProposedDeclarations.IdOf(item)] = [new PassCitation("d-2", "items")],
            [ProposedDeclarations.IdOf(MadeOf)] = [new PassCitation("d-1", "workorders")],
        });
        var one = new ScriptedModel(_ => """{"verdict":"supported"}""");
        var two = new ScriptedModel(_ => """{"verdict":"supported"}""");

        var first = await RepeatConsistency.JudgeAsync(proposals, Source(), one, 2, At, 1, null, TestContext.Current.CancellationToken);
        var second = await RepeatConsistency.JudgeAsync(proposals, Source(), two, 2, At, 2, null, TestContext.Current.CancellationToken);

        Assert.All(one.Prompts, p => Assert.DoesNotContain("points to ('Item')", p, StringComparison.Ordinal));
        Assert.Contains(two.Prompts, p => p.Contains("points to ('Item')", StringComparison.Ordinal));
        Assert.Equal(one.Prompts.Where(p => p.Contains("An entity type named", StringComparison.Ordinal)), two.Prompts.Where(p => p.Contains("An entity type named", StringComparison.Ordinal)));
        Assert.All(first.Rows, r => Assert.Equal((1, "d18569a4"), (r.LayerVersion, r.PromptFingerprint)));
        Assert.All(second.Rows, r => Assert.Equal((2, "e2438c40"), (r.LayerVersion, r.PromptFingerprint)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => RepeatConsistency.JudgeAsync(proposals, Source(), one, 2, At, 3, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Repeats_that_differ_leave_the_proposal_wobbling_with_every_vote_kept()
    {
        var calls = 0;
        var model = new ScriptedModel(_ => ++calls % 2 == 0 ? """{"verdict":"unsure"}""" : """{"verdict":"supported"}""");

        var row = Assert.Single((await RepeatConsistency.JudgeAsync(Proposed(WorkOrder), Source(), model, 4, At, null, TestContext.Current.CancellationToken)).Rows);

        Assert.Equal("wobbling", row.Verdict);
        Assert.Equal(["supported", "unsure", "supported", "unsure"], row.Votes);
    }

    [Fact]
    public async Task A_repeat_that_fails_is_an_invalid_vote_that_says_why_and_the_asking_goes_on()
    {
        var calls = 0;
        var model = new ScriptedModel(_ => ++calls == 2 ? throw new TimeoutException("slow") : """{"verdict":"supported"}""");

        var row = Assert.Single((await RepeatConsistency.JudgeAsync(Proposed(WorkOrder), Source(), model, 3, At, null, TestContext.Current.CancellationToken)).Rows);

        Assert.Equal("incomplete", row.Verdict);
        Assert.Equal(["supported", "invalid", "supported"], row.Votes);
        Assert.Contains("TimeoutException", row.Note, StringComparison.Ordinal);
        Assert.Equal(3, model.Prompts.Count);
    }

    [Fact]
    public async Task A_proposal_none_of_whose_records_can_be_read_is_incomplete_and_the_model_is_not_asked()
    {
        var model = new ScriptedModel(_ => """{"verdict":"supported"}""");
        var source = new FakeSource { Down = true };

        var row = Assert.Single((await RepeatConsistency.JudgeAsync(Proposed(WorkOrder), source, model, 5, At, null, TestContext.Current.CancellationToken)).Rows);

        Assert.Equal((ShadowVerdict.Incomplete, 0), (row.Verdict, model.Prompts.Count));
        Assert.Empty(row.Votes!);
    }

    [Fact]
    public async Task A_pass_with_more_proposals_than_a_run_may_ask_about_is_stopped_before_any_call()
    {
        var names = Enumerable.Range(0, RepeatConsistency.MaxItems + 1).Select(i => new EntityTypeDeclaration($"T{i}", "mes", "t", "k", new Dictionary<string, string>())).ToArray();
        var model = new ScriptedModel(_ => """{"verdict":"supported"}""");

        await Assert.ThrowsAsync<ArgumentException>(() => RepeatConsistency.JudgeAsync(Proposed(names), Source(), model, 5, At, null, TestContext.Current.CancellationToken));

        Assert.Empty(model.Prompts);
    }

    [Fact]
    public async Task A_proposal_the_pass_kept_no_records_for_is_not_asked_about()
    {
        var model = new ScriptedModel(_ => """{"verdict":"supported"}""");
        var proposals = new ProposedDeclarations("pass-1", [WorkOrder], null, new Dictionary<string, IReadOnlyList<PassCitation>>());

        var result = await RepeatConsistency.JudgeAsync(proposals, Source(), model, 5, At, null, TestContext.Current.CancellationToken);

        Assert.Empty(result.Rows);
        Assert.Equal(["entityType:WorkOrder"], result.NotJudged);
        Assert.Empty(model.Prompts);
    }

    [Fact]
    public async Task What_the_repeats_said_is_kept_in_the_ledger_and_read_back()
    {
        var model = new ScriptedModel(_ => """{"verdict":"unsure"}""");
        var row = Assert.Single((await RepeatConsistency.JudgeAsync(Proposed(WorkOrder), Source(), model, 3, At, null, TestContext.Current.CancellationToken)).Rows);

        var read = ShadowLedger.Parse(ShadowLedger.Write(row));

        Assert.Equal((row.Verdict, row.PromptFingerprint), (read.Verdict, read.PromptFingerprint));
        Assert.Equal(["unsure", "unsure", "unsure"], read.Votes);
    }

    private sealed class ScriptedModel(Func<string, string> answer) : IModelClient
    {
        public List<string> Prompts { get; } = [];

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Prompts.Add(request.Prompt);
            return Task.FromResult(new ModelResponse(answer(request.Prompt)));
        }
    }

    private sealed class FakeSource : ICitationSource
    {
        public bool Down { get; init; }

        public Task<IReadOnlyDictionary<string, CitedReading>> ReadAsync(string subject, IReadOnlyCollection<string> recordIds, CancellationToken cancellationToken)
        {
            if (Down)
            {
                throw new InvalidOperationException("down");
            }

            return Task.FromResult<IReadOnlyDictionary<string, CitedReading>>(recordIds.ToDictionary(
                id => id, _ => new CitedReading(CitedStanding.Standing, new Dictionary<string, string?> { ["wo"] = "WO-1", ["item"] = "I-100" })));
        }
    }
}
