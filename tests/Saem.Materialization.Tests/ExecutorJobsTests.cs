using System.Text.Json;
using Saem.Contracts;
using Saem.Ontology;
using Saem.Query;
using Saem.Materialization.Cli;
using Xunit;

namespace Saem.Materialization.Tests;

/// <summary>
/// saem D-90: the kinds of job an executor does are the commands of a one-shot run, on what the group's account reads
/// and the store the group keeps — the same lines out, the same ending.
/// </summary>
public sealed class ExecutorJobsTests : IDisposable
{
    private static readonly MaterializeOptions Run = new(
        "ontology.json", "sources.json", "g-plant", new Uri("https://example.org/ontology/"), new Uri("http://plant.test/"), null, null);

    private static readonly Ontology.Ontology Plant = Ontology.Ontology.Create(
    [
        new EntityTypeDeclaration("WorkOrder", "mes", "work_orders", "wo_no", new Dictionary<string, string> { ["item"] = "item_code" }),
        new EntityTypeDeclaration("Item", "erp", "items", "item_code", new Dictionary<string, string>()),
        new RelationDeclaration("orders_item", "WorkOrder", "item", "Item"),
    ]);

    private static readonly EntityIris Iris = new(new Uri("https://example.org/ontology/"));

    private readonly FakeFormbase _formbase = new();

    public void Dispose() => _formbase.Dispose();

    private readonly EdgeMaterializerTests.Sessions _sessions = new(new Dictionary<string, List<SourceRow>>
    {
        ["mes/work_orders"] = [EdgeMaterializerTests.Row("WO-1", ("item_code", "I-100")), EdgeMaterializerTests.Row("WO-2", ("item_code", null))],
        ["erp/items"] = [EdgeMaterializerTests.Row("I-100")],
    });

    private IReadOnlyDictionary<string, JobWork> Kinds() => ExecutorJobs.For(Run, _ => Task.FromResult(new GroupContext(
        Plant,
        new Dictionary<string, SourceOpener> { ["mes"] = ct => _sessions.OpenAsync("mes", ct), ["erp"] = ct => _sessions.OpenAsync("erp", ct) },
        new HttpClient(_formbase, disposeHandler: false) { BaseAddress = _formbase.Client.BaseAddress })));

    private async Task<(JobOutcome Outcome, string Output)> RunAsync(string kind, JobArguments? arguments = null, string requestedBy = "person-1")
    {
        var output = new StringWriter();
        var outcome = await Kinds()[kind](new JobCall(requestedBy, arguments ?? new JobArguments()), output, TestContext.Current.CancellationToken);
        return (outcome, output.ToString());
    }

    [Fact]
    public async Task A_pass_writes_the_edges_and_says_so_as_the_command_does()
    {
        var (outcome, output) = await RunAsync("pass");

        Assert.True(outcome.Succeeded);
        Assert.StartsWith("group g-plant: 1 edge(s) written, 0 unchanged, 0 retired;", output);
        Assert.Single(_formbase.Stream);
    }

    [Fact]
    public async Task Verify_agrees_after_a_pass_and_disagrees_before_it_writing_nothing()
    {
        var before = await RunAsync("verify");
        Assert.Empty(_formbase.Stream);

        await RunAsync("pass");
        var after = await RunAsync("verify");

        Assert.False(before.Outcome.Succeeded);
        Assert.True(after.Outcome.Succeeded);
    }

    [Fact]
    public async Task A_changed_pass_after_a_whole_one_finds_nothing_more_to_write()
    {
        await RunAsync("pass");

        var (outcome, output) = await RunAsync("changed-pass");

        Assert.True(outcome.Succeeded);
        Assert.Contains("0 edge(s) written, 1 unchanged", output);
    }

    [Fact]
    public async Task A_check_files_its_findings_as_the_report()
    {
        var (outcome, output) = await RunAsync("check");

        Assert.True(outcome.Succeeded);
        Assert.Contains("WorkOrder [read]", output);
        using var report = JsonDocument.Parse(outcome.Report!);
        Assert.Equal(JsonValueKind.Object, report.RootElement.ValueKind);
    }

    [Fact]
    public async Task A_judgment_is_filed_as_the_requesters_ground()
    {
        var duplicates = Ontology.Ontology.Create(
        [
            .. Plant.EntityTypes.Values,
            new IdentityDeclaration("same_order", "WorkOrder", "WorkOrder", IdentityDecision.Automatic) { Match = [new FieldPair("item", "item")] },
        ]);
        var kinds = ExecutorJobs.For(Run, _ => Task.FromResult(new GroupContext(
            duplicates, new Dictionary<string, SourceOpener>(), new HttpClient(_formbase, disposeHandler: false) { BaseAddress = _formbase.Client.BaseAddress })));
        var output = new StringWriter();

        var outcome = await kinds["judge"](
            new JobCall("p-3", new JobArguments("same_order", "wo/2", "wo/1", Same: false)), output, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        var ground = Assert.Single(_formbase.Type("identity_grounds").Rows);
        Assert.Equal(("0", "p-3"), ((string?)ground.Body!["score"], (string?)ground.Body["judged_by"]));
        Assert.Equal("same_order: wo/2 and wo/1 judged two things by p-3; the next pass takes it in.", output.ToString().Trim());
    }

    [Fact]
    public async Task The_candidates_are_listed_as_the_report_with_only_iris_and_scores()
    {
        var people = Ontology.Ontology.Create(
        [
            .. Plant.EntityTypes.Values,
            new IdentityDeclaration("same_order", "WorkOrder", "WorkOrder", IdentityDecision.Confirmed) { Match = [new FieldPair("item", "item")] },
        ]);
        var kinds = ExecutorJobs.For(Run, _ => Task.FromResult(new GroupContext(
            people, new Dictionary<string, SourceOpener>(), new HttpClient(_formbase, disposeHandler: false) { BaseAddress = _formbase.Client.BaseAddress })));
        var store = new FormbaseIdentityStore(new HttpClient(_formbase, disposeHandler: false) { BaseAddress = _formbase.Client.BaseAddress });
        async IAsyncEnumerable<IdentityCluster> Found()
        {
            yield return new IdentityCluster("same_order", [Iris.For("WorkOrder", "WO 1"), Iris.For("WorkOrder", "WO/2")])
            {
                Grounds = [new IdentityGround(Iris.For("WorkOrder", "WO 1"), Iris.For("WorkOrder", "WO/2"), "match", 1)],
            };
            await Task.CompletedTask;
        }

        await store.ReplaceAsync(people, Found(), TestContext.Current.CancellationToken);
        var output = new StringWriter();

        var outcome = await kinds["candidates"](new JobCall("person-1", new JobArguments()), output, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.Equal("group g-plant: 1 pair(s) await a person's judgment.", output.ToString().Trim());
        using var report = JsonDocument.Parse(outcome.Report!);
        var pair = Assert.Single(report.RootElement.GetProperty("candidates").EnumerateArray());
        Assert.Equal(
            ("same_order", Iris.For("WorkOrder", "WO 1"), Iris.For("WorkOrder", "WO/2"), 1.0),
            (pair.GetProperty("identity").GetString(), pair.GetProperty("first").GetString(), pair.GetProperty("second").GetString(), pair.GetProperty("score").GetDouble()));
        // Each entity comes with its type and key, as the IRI was minted from them — the reader does not take the IRI apart.
        Assert.Equal(("WorkOrder", "WO 1"), (pair.GetProperty("firstEntity").GetProperty("type").GetString(), pair.GetProperty("firstEntity").GetProperty("key").GetString()));
        Assert.Equal("WO/2", pair.GetProperty("secondEntity").GetProperty("key").GetString());
    }

    [Fact]
    public async Task The_status_is_counts_and_times_after_a_pass()
    {
        var empty = await RunAsync("status");
        using var before = JsonDocument.Parse(empty.Outcome.Report!);
        Assert.Equal(JsonValueKind.Null, before.RootElement.GetProperty("status").GetProperty("asOf").ValueKind);

        await RunAsync("pass");
        var (outcome, output) = await RunAsync("status");

        Assert.True(outcome.Succeeded);
        Assert.Equal("group g-plant: 1 edge(s) over 1 relation(s), 0 thing(s) over 0 identity(ies).", output.Trim());
        using var report = JsonDocument.Parse(outcome.Report!);
        var status = report.RootElement.GetProperty("status");
        Assert.NotEqual(JsonValueKind.Null, status.GetProperty("asOf").ValueKind);
        var relation = Assert.Single(status.GetProperty("relations").EnumerateArray());
        Assert.Equal(("orders_item", 1), (relation.GetProperty("relation").GetString(), relation.GetProperty("edges").GetInt32()));
    }

    [Theory]
    [InlineData(null, "wo/1", "wo/2", true, "identity is required")]
    [InlineData("same_order", null, "wo/2", true, "first is required")]
    [InlineData("same_order", "wo/1", null, true, "second is required")]
    [InlineData("same_order", "wo/1", "wo/2", null, "same is required")]
    public async Task A_judgment_that_names_no_pair_or_no_verdict_is_refused(string? identity, string? first, string? second, bool? same, string message)
    {
        var refused = await Assert.ThrowsAsync<MaterializeUsageException>(() => RunAsync("judge", new JobArguments(identity, first, second, same)));

        Assert.Contains(message, refused.Message);
    }

    [Fact]
    public async Task A_fit_needs_an_identity_to_fit()
    {
        var refused = await Assert.ThrowsAsync<MaterializeUsageException>(() => RunAsync("fit"));

        Assert.Contains("identity is required", refused.Message);
    }

    [Fact]
    public async Task What_a_job_works_on_is_released_when_it_ends()
    {
        await RunAsync("verify");

        // The one system the relations leave from is the one opened, and the one closed.
        Assert.Equal(1, _sessions.Closed);
    }
}
