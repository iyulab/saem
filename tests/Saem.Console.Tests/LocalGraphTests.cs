using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Saem.Console.Tests;

/// <summary>
/// saem D-89 ①: a local graph is a lookup the console asks the MCP server as the person looking — the person's console
/// token exchanged for one meant for the server, never sent on itself — and the answer is the server's, unchanged.
/// </summary>
public sealed class LocalGraphTests
{
    private static readonly JsonElement Call = JsonSerializer.SerializeToElement(new
    {
        start = new { type = "Equipment", key = "1017" },
        path = new[] { "equipment_on" },
        read = new[] { "name" },
    });

    [Fact]
    public async Task A_builder_gets_the_servers_answer_as_it_is_and_an_auditor_is_forbidden()
    {
        var lookups = new RecordingLookups();
        await using var server = await ConsoleServer.StartAsync(configure: services => services.AddSingleton<IConsoleLookups>(lookups));
        var token = ConsoleServer.Token("alice", ["ontology-builders"]);
        using var builder = server.Client(token);
        using var auditor = server.Client(ConsoleServer.Token("ann", ["privacy-office"]));

        using var response = await builder.PostAsJsonAsync("api/graph/local", Call, TestContext.Current.CancellationToken);
        var answer = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal("refused", answer.GetProperty("kind").GetString());
        Assert.Equal(token, lookups.Presented);
        Assert.Equal("1017", lookups.Arguments.GetProperty("start").GetProperty("key").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PostAsJsonAsync("api/graph/local", Call, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task The_server_is_asked_with_the_exchanged_token_and_never_the_consoles_own()
    {
        var issuer = new ExchangingIssuer();
        string? sent = null;
        var lookups = new McpConsoleLookups(
            new HttpClient(issuer),
            Options.Create(new ConsoleLookupOptions { McpEndpoint = "http://mcp.test/mcp", TokenEndpoint = "http://issuer.test/token" }),
            Options.Create(new ConsoleIdentityOptions { ClientId = "saem-console", ClientSecret = "s3cret" }),
            (_, token, _, _) =>
            {
                sent = token;
                return Task.FromResult(JsonSerializer.SerializeToElement(new { kind = "answered" }));
            });

        await lookups.LookupAsync("console-token-of-alice", Call, TestContext.Current.CancellationToken);

        Assert.Equal("token-for-saem", sent);
        Assert.Equal("console-token-of-alice", issuer.Form["subject_token"]);
        Assert.Equal("saem", issuer.Form["audience"]);
        Assert.Equal("urn:ietf:params:oauth:grant-type:token-exchange", issuer.Form["grant_type"]);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("saem-console:s3cret")), issuer.Authorization);
        // The console names itself as the actor: its own client-credentials token, asked for first (RFC 8693 delegation).
        Assert.Equal(["client_credentials", "urn:ietf:params:oauth:grant-type:token-exchange"], issuer.Grants);
        Assert.Equal("token-for-saem", issuer.Form["actor_token"]);
        Assert.Equal("urn:ietf:params:oauth:token-type:access_token", issuer.Form["actor_token_type"]);
    }

    [Fact]
    public async Task Without_presenting_an_actor_the_exchange_is_the_only_request_and_names_none()
    {
        var issuer = new ExchangingIssuer();
        var lookups = new McpConsoleLookups(
            new HttpClient(issuer),
            Options.Create(new ConsoleLookupOptions { McpEndpoint = "http://mcp.test/mcp", TokenEndpoint = "http://issuer.test/token", PresentActor = false }),
            Options.Create(new ConsoleIdentityOptions { ClientId = "saem-console" }),
            (_, _, _, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { kind = "answered" })));

        await lookups.LookupAsync("console-token-of-alice", Call, TestContext.Current.CancellationToken);

        Assert.Equal(["urn:ietf:params:oauth:grant-type:token-exchange"], issuer.Grants);
        Assert.False(issuer.Form.ContainsKey("actor_token"));
    }

    [Fact]
    public async Task A_refused_exchange_asks_nothing_and_is_a_bad_gateway()
    {
        var called = false;
        var lookups = new McpConsoleLookups(
            new HttpClient(new ExchangingIssuer(refuse: true)),
            Options.Create(new ConsoleLookupOptions { McpEndpoint = "http://mcp.test/mcp", TokenEndpoint = "http://issuer.test/token" }),
            Options.Create(new ConsoleIdentityOptions { ClientId = "saem-console" }),
            (_, _, _, _) =>
            {
                called = true;
                return Task.FromResult(default(JsonElement));
            });
        await using var server = await ConsoleServer.StartAsync(configure: services => services.AddSingleton<IConsoleLookups>(lookups));
        using var builder = server.Client(ConsoleServer.Token("alice", ["ontology-builders"]));

        using var response = await builder.PostAsJsonAsync("api/graph/local", Call, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.False(called);
    }

    private sealed class RecordingLookups : IConsoleLookups
    {
        public string? Presented { get; private set; }

        public JsonElement Arguments { get; private set; }

        public Task<JsonElement> LookupAsync(string presentedToken, JsonElement arguments, CancellationToken cancellationToken)
        {
            Presented = presentedToken;
            Arguments = arguments.Clone();
            return Task.FromResult(JsonSerializer.SerializeToElement(new { kind = "refused", reason = "noVisibleResult" }));
        }
    }

    private sealed class ExchangingIssuer(bool refuse = false) : HttpMessageHandler
    {
        public Dictionary<string, string> Form { get; } = [];

        public string? Authorization { get; private set; }

        public List<string> Grants { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            foreach (var pair in (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&'))
            {
                var at = pair.IndexOf('=', StringComparison.Ordinal);
                Form[Uri.UnescapeDataString(pair[..at])] = Uri.UnescapeDataString(pair[(at + 1)..].Replace('+', ' '));
            }

            Grants.Add(Form["grant_type"]);

            return refuse
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"unauthorized_client"}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"token-for-saem","token_type":"Bearer"}""") };
        }
    }
}
