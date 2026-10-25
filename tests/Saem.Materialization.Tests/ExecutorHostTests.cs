using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using Saem.Materialization.Cli;
using Xunit;

namespace Saem.Materialization.Tests;

/// <summary>
/// saem D-90: the executor kept running asks nothing of a caller but one of its secrets, does one job at a time, and
/// tells a caller what a job printed and how it ended.
/// </summary>
public sealed class ExecutorHostTests : IAsyncLifetime
{
    private const string Secret = "first-secret-of-at-least-24-chars";
    private const string Next = "next-secret-of-at-least-24-chars!";

    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        var kinds = new Dictionary<string, JobWork>(StringComparer.Ordinal)
        {
            ["echo"] = async (output, _) =>
            {
                await output.WriteLineAsync("first line");
                await output.WriteLineAsync("second line");
                return new JobOutcome(true, """{"findings":[]}""");
            },
            ["wait"] = async (output, cancellationToken) =>
            {
                await output.WriteLineAsync("waiting");
                await _release.Task.WaitAsync(cancellationToken);
                return new JobOutcome(true);
            },
            ["refuse"] = (output, _) => Task.FromResult(new JobOutcome(false)),
            ["misuse"] = (_, _) => throw new MaterializeUsageException("--sources is required."),
            ["crash"] = (_, _) => throw new ArithmeticException("a detail the caller is not told"),
        };
        _app = ExecutorHost.Build(new Uri("http://127.0.0.1:0"), [Secret, Next], kinds);
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First() + "/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
    }

    public async ValueTask DisposeAsync()
    {
        _release.TrySetResult();
        _http.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task A_caller_without_one_of_the_secrets_is_refused()
    {
        using var bare = new HttpClient { BaseAddress = _http.BaseAddress };
        foreach (var header in new AuthenticationHeaderValue?[] { null, new("Bearer", "not-the-secret"), new("Basic", Secret) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "jobs") { Headers = { Authorization = header } };
            using var response = await bare.SendAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
        }
    }

    [Fact]
    public async Task Either_of_two_secrets_asks_for_work()
    {
        using var bare = new HttpClient { BaseAddress = _http.BaseAddress };
        using var request = new HttpRequestMessage(HttpMethod.Get, "jobs") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", Next) } };
        using var response = await bare.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_job_runs_and_says_what_it_printed_how_it_ended_and_who_asked()
    {
        var accepted = await Post("echo", "person-1");
        Assert.Equal(HttpStatusCode.Accepted, accepted.Status);

        var job = await Finished(accepted.Id);

        Assert.Equal("succeeded", job.GetProperty("state").GetString());
        Assert.Equal(0, job.GetProperty("exitCode").GetInt32());
        Assert.Equal("first line\nsecond line\n", job.GetProperty("output").GetString()!.ReplaceLineEndings("\n"));
        Assert.Equal("person-1", job.GetProperty("requestedBy").GetString());
        Assert.Equal("echo", job.GetProperty("kind").GetString());
        Assert.Equal("""{"findings":[]}""", job.GetProperty("report").GetString());
        Assert.NotEqual(JsonValueKind.Null, job.GetProperty("finishedAt").ValueKind);
    }

    [Fact]
    public async Task A_second_job_while_one_runs_is_refused_naming_the_one_in_the_way()
    {
        var first = await Post("wait", "person-1");
        var second = await Post("echo", "person-2");

        Assert.Equal(HttpStatusCode.Conflict, second.Status);
        Assert.Equal(first.Id, second.Body.GetProperty("running").GetProperty("id").GetString());

        _release.SetResult();
        Assert.Equal("succeeded", (await Finished(first.Id)).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Accepted, (await Post("echo", "person-2")).Status);
    }

    [Fact]
    public async Task A_running_job_is_cancelled_and_says_so()
    {
        var running = await Post("wait", "person-1");

        using var cancel = await _http.PostAsync($"jobs/{running.Id}/cancel", null, TestContext.Current.CancellationToken);
        var job = await Finished(running.Id);

        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        Assert.Equal("cancelled", job.GetProperty("state").GetString());
        Assert.Equal(130, job.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task Cancelling_a_job_that_is_finished_or_unknown_is_refused()
    {
        var done = await Post("echo", "person-1");
        await Finished(done.Id);

        using var finished = await _http.PostAsync($"jobs/{done.Id}/cancel", null, TestContext.Current.CancellationToken);
        using var unknown = await _http.PostAsync("jobs/nothing/cancel", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, finished.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_job_that_does_not_hold_fails_with_status_one_and_a_usage_error_with_status_two()
    {
        var refused = await Finished((await Post("refuse", "p")).Id);
        var misused = await Finished((await Post("misuse", "p")).Id);

        Assert.Equal(("failed", 1), (refused.GetProperty("state").GetString(), refused.GetProperty("exitCode").GetInt32()));
        Assert.Equal(("failed", 2), (misused.GetProperty("state").GetString(), misused.GetProperty("exitCode").GetInt32()));
        Assert.Contains("--sources is required.", misused.GetProperty("output").GetString());
    }

    [Fact]
    public async Task An_unexpected_failure_is_reported_without_its_detail()
    {
        var job = await Finished((await Post("crash", "p")).Id);

        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Contains("ArithmeticException", job.GetProperty("output").GetString());
        Assert.DoesNotContain("a detail the caller is not told", job.GetProperty("output").GetString());
    }

    [Theory]
    [InlineData("""{"kind":"nothing","requestedBy":"p"}""")]
    [InlineData("""{"requestedBy":"p"}""")]
    [InlineData("""{"kind":"echo"}""")]
    [InlineData("""{"kind":"echo","requestedBy":""}""")]
    [InlineData("not json")]
    public async Task A_request_that_names_no_known_kind_or_no_one_is_refused(string body)
    {
        using var response = await _http.PostAsync("jobs", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_recent_jobs_are_listed_newest_first_and_an_unknown_one_is_not_found()
    {
        var first = await Post("echo", "p");
        await Finished(first.Id);
        var second = await Post("refuse", "p");
        await Finished(second.Id);

        var list = await _http.GetFromJsonAsync<JsonElement>("jobs", TestContext.Current.CancellationToken);
        using var unknown = await _http.GetAsync("jobs/nothing", TestContext.Current.CancellationToken);

        Assert.Equal([second.Id, first.Id], list.EnumerateArray().Select(j => j.GetProperty("id").GetString()!));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task What_a_job_prints_is_kept_up_to_a_limit_and_says_it_was_cut()
    {
        var store = new JobStore(TimeProvider.System, NullLogger.Instance, keep: 2, outputLimit: 10);
        Assert.True(store.TryStart("echo", "p", async (output, _) =>
        {
            await output.WriteAsync("0123456789abcdef");
            return new JobOutcome(true);
        }, CancellationToken.None, out var started));
        await store.DrainAsync(TimeSpan.FromSeconds(10));

        var job = store.Get(started.Id)!;

        Assert.Equal("0123456789", job.Output);
        Assert.True(job.OutputTruncated);
    }

    [Fact]
    public async Task Only_the_last_few_jobs_are_kept()
    {
        var store = new JobStore(TimeProvider.System, NullLogger.Instance, keep: 2);
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            Assert.True(store.TryStart("echo", "p", (_, _) => Task.FromResult(new JobOutcome(true)), CancellationToken.None, out var job));
            ids.Add(job.Id);
            await store.DrainAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Null(store.Get(ids[0]));
        Assert.NotNull(store.Get(ids[2]));
    }

    private async Task<(HttpStatusCode Status, string Id, JsonElement Body)> Post(string kind, string requestedBy)
    {
        using var response = await _http.PostAsJsonAsync("jobs", new { kind, requestedBy }, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var id = body.TryGetProperty("id", out var own) ? own.GetString()! : "";
        return (response.StatusCode, id, body);
    }

    private async Task<JsonElement> Finished(string id)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var job = await _http.GetFromJsonAsync<JsonElement>($"jobs/{id}", TestContext.Current.CancellationToken);
            if (job.GetProperty("state").GetString() != "running")
            {
                return job;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Job {id} did not finish.");
    }
}
