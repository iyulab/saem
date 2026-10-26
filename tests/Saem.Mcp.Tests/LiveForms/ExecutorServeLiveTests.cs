using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Saem.Contracts;
using Saem.Materialization;
using Saem.Materialization.Cli;
using Saem.Ontology;
using Saem.Query;
using Xunit;

namespace Saem.Mcp.Tests.LiveForms;

/// <summary>
/// <c>saem-materialize serve</c> as a deployment starts it (saem D-90): its arguments and environment parsed, its catalog and
/// sources files read for each job, and a caller's HTTP requests answered over a group's real store — the part
/// <see cref="ExecutorJudgmentLiveTests"/> does not walk, since it hands the jobs their group directly.
/// </summary>
public sealed class ExecutorServeLiveTests : IAsyncLifetime
{
    private const string Secret = "live-executor-secret-of-24-chars";

    private static readonly Uri BaseIri = new("https://rig.saem.test/ontology/");

    private const string CatalogJson = """
        [
          { "kind": "entityType", "name": "WorkOrder", "system": "mes", "table": "work_orders", "key": "wo_no",
            "fields": { "item": "item_code" } },
          { "kind": "identity", "name": "same_order", "first": "WorkOrder", "second": "WorkOrder", "decision": "confirmed",
            "match": [ { "first": "item", "second": "item" } ] }
        ]
        """;

    private GroupStoreRig _store = null!;
    private string _directory = null!;
    private WebApplication _executor = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _store = await GroupStoreRig.StartAsync("g-plant", ct);
        _directory = Directory.CreateTempSubdirectory("saem-serve-live-").FullName;
        var catalog = Path.Combine(_directory, "ontology.json");
        var sources = Path.Combine(_directory, "sources.json");
        await File.WriteAllTextAsync(catalog, CatalogJson, ct);
        // No system is read by the jobs this walks, so no account is named: the sources file is the empty object a group with no source starts with.
        await File.WriteAllTextAsync(sources, "{}", ct);

        // The arguments a deployment gives `saem-materialize serve`, and the environment it sets.
        var environment = new Dictionary<string, string> { [ServeOptions.SecretVariable] = Secret };
        var serve = ServeOptions.Parse(
            ["--catalog", catalog, "--sources", sources, "--group", "g-plant", "--base-iri", BaseIri.ToString(),
             "--store", _store.Formbase.BaseAddress!.ToString(), "--listen", "http://127.0.0.1:0"],
            name => environment.GetValueOrDefault(name));
        _executor = ExecutorHost.Build(serve.Listen, serve.Secrets, ExecutorJobs.ForFiles(serve.Run, name => environment.GetValueOrDefault(name)));
        await _executor.StartAsync(ct);
        _http = new HttpClient { BaseAddress = new Uri(_executor.Urls.First() + "/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _executor.DisposeAsync();
        await _store.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task A_caller_lists_judges_and_counts_the_pairs_of_a_group_over_http()
    {
        var ct = TestContext.Current.CancellationToken;
        var ontology = await new FileOntologyCatalog(Path.Combine(_directory, "ontology.json")).GetAsync(ct);
        var iris = new EntityIris(BaseIri);
        string A(string key) => iris.For("WorkOrder", key);
        await new FormbaseIdentityStore(_store.Formbase).ReplaceAsync(ontology, Found(A), ct);

        using var bare = new HttpClient { BaseAddress = _http.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await bare.GetAsync("jobs", ct)).StatusCode);

        var listed = await RunAsync("candidates", null, ct);
        Assert.Equal("succeeded", listed.GetProperty("state").GetString());
        var pairs = Pairs(listed);
        Assert.Equal(2, pairs.Count);

        var judged = await RunAsync("judge", new { identity = "same_order", first = pairs[0].First, second = pairs[0].Second, same = false }, ct);
        Assert.Equal("succeeded", judged.GetProperty("state").GetString());
        Assert.Equal("person-7", judged.GetProperty("requestedBy").GetString());
        Assert.Contains("judged two things by person-7", judged.GetProperty("output").GetString());

        Assert.Single(Pairs(await RunAsync("candidates", null, ct)));

        var status = await RunAsync("status", null, ct);
        using var report = JsonDocument.Parse(status.GetProperty("report").GetString()!);
        var identity = Assert.Single(report.RootElement.GetProperty("status").GetProperty("identities").EnumerateArray());
        Assert.Equal(("same_order", 1), (identity.GetProperty("identity").GetString(), identity.GetProperty("awaiting").GetInt32()));
    }

    private static async IAsyncEnumerable<IdentityCluster> Found(Func<string, string> a)
    {
        yield return new IdentityCluster("same_order", [a("WO-1"), a("WO-2"), a("WO-3")])
        {
            Grounds = [new IdentityGround(a("WO-1"), a("WO-2"), "match", 1), new IdentityGround(a("WO-2"), a("WO-3"), "match", 1)],
        };
        await Task.CompletedTask;
    }

    private async Task<JsonElement> RunAsync(string kind, object? arguments, CancellationToken ct)
    {
        using var accepted = await _http.PostAsJsonAsync("jobs", new { kind, requestedBy = "person-7", arguments }, ct);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var id = (await accepted.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetString();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var job = await _http.GetFromJsonAsync<JsonElement>($"jobs/{id}", ct);
            if (job.GetProperty("state").GetString() != "running")
            {
                return job;
            }

            Assert.True(DateTime.UtcNow < deadline, $"The {kind} job did not finish.");
            await Task.Delay(100, ct);
        }
    }

    private static List<(string First, string Second)> Pairs(JsonElement job)
    {
        using var report = JsonDocument.Parse(job.GetProperty("report").GetString()!);
        return [.. report.RootElement.GetProperty("candidates").EnumerateArray()
            .Select(c => (c.GetProperty("first").GetString()!, c.GetProperty("second").GetString()!))];
    }
}
