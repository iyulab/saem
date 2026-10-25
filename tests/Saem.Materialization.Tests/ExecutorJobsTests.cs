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

    private async Task<(JobOutcome Outcome, string Output)> RunAsync(string kind, JobArguments? arguments = null)
    {
        var output = new StringWriter();
        var outcome = await Kinds()[kind](arguments ?? new JobArguments(), output, TestContext.Current.CancellationToken);
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
