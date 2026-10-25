using System.Text.Json;
using Saem.Contracts;
using Saem.Materialization;
using Saem.Materialization.Cli;
using Saem.Ontology;
using Saem.Query;
using Xunit;

namespace Saem.Mcp.Tests.LiveForms;

/// <summary>
/// The executor's reads and judgments over a group's real store (saem D-90): the grounds an identity found are filed in a
/// Formbase projecting into a MorphDB project of its own, and the executor lists the pairs no one has judged, takes a
/// person's judgment, and reports what the store holds — the same jobs a console asks for, over the store a deployment has.
/// </summary>
public sealed class ExecutorJudgmentLiveTests : IAsyncLifetime
{
    private static readonly EntityIris Iris = new(new Uri("https://rig.saem.test/ontology/"));

    private static readonly MaterializeOptions Run = new(
        "ontology.json", "sources.json", "g-plant", new Uri("https://rig.saem.test/ontology/"), new Uri("http://plant.test/"), null, null);

    private static readonly Ontology.Ontology Plant = Ontology.Ontology.Create(
    [
        new EntityTypeDeclaration("WorkOrder", "mes", "work_orders", "wo_no", new Dictionary<string, string> { ["item"] = "item_code" }),
        new IdentityDeclaration("same_order", "WorkOrder", "WorkOrder", IdentityDecision.Confirmed) { Match = [new FieldPair("item", "item")] },
    ]);

    private GroupStoreRig _store = null!;

    public async ValueTask InitializeAsync() => _store = await GroupStoreRig.StartAsync("g-plant", TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await _store.DisposeAsync();

    private IReadOnlyDictionary<string, JobWork> Kinds() => ExecutorJobs.For(Run, _ => Task.FromResult(new GroupContext(
        Plant, new Dictionary<string, SourceOpener>(), new HttpClient { BaseAddress = _store.Formbase.BaseAddress })));

    private async Task<(JobOutcome Outcome, string Output)> RunAsync(string kind, JobArguments? arguments = null)
    {
        var output = new StringWriter();
        var outcome = await Kinds()[kind](new JobCall("person-7", arguments ?? new JobArguments()), output, TestContext.Current.CancellationToken);
        return (outcome, output.ToString());
    }

    private static async IAsyncEnumerable<IdentityCluster> Found()
    {
        string A(string key) => Iris.For("WorkOrder", key);
        yield return new IdentityCluster("same_order", [A("WO-1"), A("WO-2"), A("WO-3")])
        {
            Grounds = [new IdentityGround(A("WO-1"), A("WO-2"), "match", 1), new IdentityGround(A("WO-2"), A("WO-3"), "match", 1)],
        };
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_pairs_no_one_has_judged_are_listed_judged_and_counted_over_the_real_store()
    {
        var ct = TestContext.Current.CancellationToken;
        await new FormbaseIdentityStore(_store.Formbase).ReplaceAsync(Plant, Found(), ct);

        var (listed, _) = await RunAsync("candidates");
        var before = Pairs(listed);
        Assert.Equal(2, before.Count);

        var first = before[0];
        var (judged, line) = await RunAsync("judge", new JobArguments("same_order", first.First, first.Second, Same: false));
        var (relisted, _) = await RunAsync("candidates");
        var (status, _) = await RunAsync("status");

        Assert.True(judged.Succeeded);
        Assert.Contains("judged two things by person-7", line);
        var after = Pairs(relisted);
        Assert.Equal(before.Count - 1, after.Count);
        Assert.DoesNotContain(after, p => p.First == first.First && p.Second == first.Second);
        using var report = JsonDocument.Parse(status.Report!);
        var identity = Assert.Single(report.RootElement.GetProperty("status").GetProperty("identities").EnumerateArray());
        Assert.Equal(("same_order", 1), (identity.GetProperty("identity").GetString(), identity.GetProperty("awaiting").GetInt32()));
        // Each entity comes with the type and key its IRI was minted from.
        Assert.Equal("WorkOrder", before[0].Type);
    }

    private static List<(string First, string Second, string Type)> Pairs(JobOutcome outcome)
    {
        using var report = JsonDocument.Parse(outcome.Report!);
        return [.. report.RootElement.GetProperty("candidates").EnumerateArray()
            .Select(c => (c.GetProperty("first").GetString()!, c.GetProperty("second").GetString()!, c.GetProperty("firstEntity").GetProperty("type").GetString()!))];
    }
}
