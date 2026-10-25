using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Saem.Console.Tests;

/// <summary>
/// saem D-90 · PLAN slice 5d: a builder asks a group's executor for work through the console, which checks the request,
/// names the caller as who asked, holds no key to the group's store, and cuts from a check what the executor's account
/// read and the builder may not.
/// </summary>
public sealed class ExecutorsApiTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static async Task<(ConsoleServer Server, HttpClient Builder, FakeExecutors Executors)> StartAsync(FakeExecutors? executors = null)
    {
        executors ??= new FakeExecutors();
        var server = await ConsoleServer.StartAsync(configure: services => services.AddSingleton<IConsoleExecutors>(executors));
        return (server, server.Client(ConsoleServer.Token("alice", ["ontology-builders"])), executors);
    }

    [Fact]
    public async Task The_groups_with_an_executor_are_listed_for_builders_only()
    {
        var (server, builder, _) = await StartAsync();
        await using var scope = server;
        using var auditor = server.Client(ConsoleServer.Token("ann", ["privacy-office"]));
        using var nobody = server.Client();

        var groups = await builder.GetFromJsonAsync<JsonElement>("api/executors", TestContext.Current.CancellationToken);

        Assert.Equal(["plant", "yard"], groups.EnumerateArray().Select(g => g.GetProperty("group").GetString()!));
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.GetAsync("api/executors", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await nobody.GetAsync("api/executors", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task A_job_is_asked_in_the_name_of_the_caller_whatever_the_request_says()
    {
        var (server, builder, executors) = await StartAsync();
        await using var scope = server;

        using var response = await builder.PostAsJsonAsync(
            "api/executors/plant/jobs", new { kind = "pass", requestedBy = "mallory", arguments = new { identity = "ignored" } }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var sent = Assert.Single(executors.Requests);
        Assert.Equal(("plant", "POST", "jobs"), (sent.Group, sent.Method, sent.Path));
        Assert.Equal("alice", sent.Body!["requestedBy"]!.GetValue<string>());
        Assert.Equal("pass", sent.Body["kind"]!.GetValue<string>());
        Assert.Empty(sent.Body["arguments"]!.AsObject());
    }

    [Fact]
    public async Task A_fit_and_a_judgment_carry_what_they_take_and_nothing_else()
    {
        var (server, builder, executors) = await StartAsync();
        await using var scope = server;

        await builder.PostAsJsonAsync("api/executors/plant/jobs", new { kind = "fit", arguments = new { identity = "same_order", first = "dropped" } }, TestContext.Current.CancellationToken);
        await builder.PostAsJsonAsync(
            "api/executors/plant/jobs", new { kind = "judge", arguments = new { identity = "same_order", first = "wo/1", second = "wo/2", same = false } }, TestContext.Current.CancellationToken);

        Assert.Equal("""{"identity":"same_order"}""", executors.Requests[0].Body!["arguments"]!.ToJsonString());
        Assert.Equal("""{"identity":"same_order","first":"wo/1","second":"wo/2","same":false}""", executors.Requests[1].Body!["arguments"]!.ToJsonString());
    }

    [Theory]
    [InlineData("""{"kind":"nothing"}""")]
    [InlineData("""{}""")]
    [InlineData("""{"kind":"fit"}""")]
    [InlineData("""{"kind":"fit","arguments":{"identity":""}}""")]
    [InlineData("""{"kind":"judge","arguments":{"identity":"i","first":"a","second":"b"}}""")]
    [InlineData("""{"kind":"judge","arguments":{"identity":"i","first":"a","same":true}}""")]
    [InlineData("""{"kind":"judge","arguments":{"identity":"i","first":"a","second":"b","same":"yes"}}""")]
    public async Task A_request_the_executor_could_not_do_is_refused_before_it_is_asked(string body)
    {
        var (server, builder, executors) = await StartAsync();
        await using var scope = server;

        using var response = await builder.PostAsync(
            "api/executors/plant/jobs", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(executors.Requests);
    }

    [Fact]
    public async Task A_group_without_an_executor_and_a_job_id_that_is_not_one_are_not_found_and_nothing_is_asked()
    {
        var (server, builder, executors) = await StartAsync();
        await using var scope = server;

        var statuses = new[]
        {
            (await builder.GetAsync("api/executors/nowhere/jobs", TestContext.Current.CancellationToken)).StatusCode,
            (await builder.GetAsync("api/executors/plant/jobs/..%2fsecrets", TestContext.Current.CancellationToken)).StatusCode,
            (await builder.PostAsync("api/executors/plant/jobs/not-an-id/cancel", null, TestContext.Current.CancellationToken)).StatusCode,
        };

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.NotFound, s));
        Assert.Empty(executors.Requests);
    }

    [Fact]
    public async Task A_check_is_shown_without_the_records_it_cites_and_without_its_lines()
    {
        var executors = new FakeExecutors
        {
            Reply = new ExecutorReply(200, JsonNode.Parse(
                """
                {"id":"0123456789abcdef0123456789abcdef","kind":"check","state":"failed","output":"  FAIL Item [key] 2 rows share a key — e.g. I-100, I-200\n",
                 "report":"{\"holds\":false,\"findings\":[{\"type\":\"Item\",\"kind\":\"key\",\"violation\":true,\"records\":[\"I-100\",\"I-200\"]}]}"}
                """)),
        };
        var (server, builder, _) = await StartAsync(executors);
        await using var scope = server;

        var job = await builder.GetFromJsonAsync<JsonElement>("api/executors/plant/jobs/0123456789abcdef0123456789abcdef", TestContext.Current.CancellationToken);

        Assert.Equal("", job.GetProperty("output").GetString());
        Assert.True(job.GetProperty("outputWithheld").GetBoolean());
        var finding = job.GetProperty("report").GetProperty("findings")[0];
        Assert.Equal(2, finding.GetProperty("recordsCited").GetInt32());
        Assert.False(finding.TryGetProperty("records", out _));
        Assert.DoesNotContain("I-100", job.GetRawText());
    }

    [Fact]
    public async Task What_a_pass_printed_comes_back_as_it_is_and_a_busy_executor_names_the_job_in_the_way()
    {
        var executors = new FakeExecutors
        {
            Reply = new ExecutorReply(409, JsonNode.Parse("""{"error":"Another job is running.","running":{"id":"0123456789abcdef0123456789abcdef","kind":"pass","state":"running","output":"group g: 3 edge(s)"}}""")),
        };
        var (server, builder, _) = await StartAsync(executors);
        await using var scope = server;

        using var response = await builder.PostAsJsonAsync("api/executors/plant/jobs", new { kind = "pass" }, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("group g: 3 edge(s)", body.GetProperty("running").GetProperty("output").GetString());
    }

    [Fact]
    public async Task An_executor_that_cannot_be_asked_is_a_bad_gateway_that_says_so_without_its_secret()
    {
        var executors = new FakeExecutors { Fail = new ExecutorUnavailableException("The executor for group 'plant' did not answer.") };
        var (server, builder, _) = await StartAsync(executors);
        await using var scope = server;

        using var response = await builder.GetAsync("api/executors/plant/jobs", TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("did not answer", text);
    }

    [Fact]
    public async Task The_executor_is_asked_over_http_with_its_own_bearer_secret_and_a_refusal_does_not_repeat_it()
    {
        var secret = "the-plant-executor-secret-123456";
        var handler = new StubHandler(request => request.Headers.Authorization?.Parameter == secret
            ? new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("""{"id":"x"}""") }
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var options = new ConsoleExecutors { ["plant"] = new() { Address = "https://executor.plant.test:8443", Secret = secret }, ["yard"] = new() { Address = "https://yard.test/", Secret = "another-secret-of-24-characters" } };
        var executors = new HttpConsoleExecutors(new StubClients(handler), Options.Create(options));

        var reply = await executors.SendAsync("plant", HttpMethod.Post, "jobs", new JsonObject { ["kind"] = "pass" }, TestContext.Current.CancellationToken);
        var refused = await Assert.ThrowsAsync<ExecutorUnavailableException>(() => executors.SendAsync("yard", HttpMethod.Get, "jobs", null, TestContext.Current.CancellationToken));

        Assert.Equal(202, reply.Status);
        Assert.Equal(new Uri("https://executor.plant.test:8443/jobs"), handler.Requests[0]);
        Assert.DoesNotContain("another-secret", refused.Message);
        Assert.Contains("Saem:Console:Executors:yard:Secret", refused.Message);
    }

    [Fact]
    public async Task An_executor_that_is_down_is_unavailable()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var executors = new HttpConsoleExecutors(
            new StubClients(handler), Options.Create(new ConsoleExecutors { ["plant"] = new() { Address = "https://e.test/", Secret = "the-plant-executor-secret-123456" } }));

        var down = await Assert.ThrowsAsync<ExecutorUnavailableException>(() => executors.SendAsync("plant", HttpMethod.Get, "jobs", null, TestContext.Current.CancellationToken));

        Assert.Contains("did not answer", down.Message);
    }

    [Theory]
    [InlineData("not-an-address", "short-secret", "http(s) address")]
    [InlineData("ftp://e.test/", "the-plant-executor-secret-123456", "http(s) address")]
    [InlineData("https://e.test/", "short", "at least 24")]
    public void Settings_that_cannot_reach_an_executor_are_named(string address, string secret, string message)
    {
        var options = new ConsoleExecutors { ["plant"] = new() { Address = address, Secret = secret } };

        Assert.Contains(message, options.Problem());
    }

    private sealed class FakeExecutors : IConsoleExecutors
    {
        public List<(string Group, string Method, string Path, JsonNode? Body)> Requests { get; } = [];

        public ExecutorReply Reply { get; init; } = new(202, JsonNode.Parse("""{"id":"0123456789abcdef0123456789abcdef","state":"running"}"""));

        public Exception? Fail { get; init; }

        public IReadOnlyCollection<string> Groups => ["yard", "plant"];

        public Task<ExecutorReply> SendAsync(string group, HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
        {
            Requests.Add((group, method.Method, path, body));
            return Fail is null ? Task.FromResult(Reply) : Task.FromException<ExecutorReply>(Fail);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(answer(request));
        }
    }

    private sealed class StubClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
