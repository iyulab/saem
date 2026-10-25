using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Saem.Mcp;
using Xunit;

namespace Saem.Console.Tests.Live;

/// <summary>
/// The console end to end against the rig (saem D-89 ①): a person's console token, exchanged at the rig's issuer for one
/// meant for the MCP server with the console as the actor, and the lookup answered by a real MCP server reading mes
/// (PostgreSQL, row-level security) as that person. The console's answer is the server's — what the source lets the
/// person see — and says the console asked for them.
/// <para>
/// The source login is the rig's interim password login: the deployment here maps pat to alice's mes login and quinn to
/// bob's, so two builders see what two different source accounts may.
/// </para>
/// </summary>
public sealed class ConsoleEndToEndLiveTests : IAsyncLifetime
{
    private const string Canary = "CANARY-7f3a-line2-only";

    private const string OntologyJson = """
        [
          { "kind": "entityType", "name": "WorkOrder", "system": "mes", "table": "work_orders", "key": "wo_no",
            "fields": { "note": "note" } }
        ]
        """;

    private static readonly string Issuer = Environment.GetEnvironmentVariable("SAEM_RIG_ISSUER")
        ?? throw new InvalidOperationException("SAEM_RIG_ISSUER names the rig's issuer (tests/rig/compose.password-login.yaml).");

    private static readonly string Mes = Environment.GetEnvironmentVariable("SAEM_RIG_MES")
        ?? throw new InvalidOperationException("SAEM_RIG_MES names the rig's mes source (tests/rig/compose.password-login.yaml).");

    private static readonly JsonElement Call = JsonSerializer.SerializeToElement(new
    {
        start = new { type = "WorkOrder", key = "WO-3" },
        path = Array.Empty<string>(),
        read = new[] { "note" },
    });

    private WebApplication _mcp = null!;
    private string _mcpOntology = "";
    private ConsoleServer _console = null!;

    public async ValueTask InitializeAsync()
    {
        _mcpOntology = Path.GetTempFileName();
        await File.WriteAllTextAsync(_mcpOntology, OntologyJson);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Saem:Ontology"] = _mcpOntology,
            ["Saem:Identity:Authority"] = Issuer,
            ["Saem:Identity:Audience"] = "saem",
            ["Saem:Identity:RequireHttpsMetadata"] = "false",
            ["Saem:Sources:Postgres:mes"] = Mes,
            ["Saem:Sources:PasswordLogins:mes:pat:User"] = "alice_pw",
            ["Saem:Sources:PasswordLogins:mes:pat:Password"] = "rig-alice-local-only",
            ["Saem:Sources:PasswordLogins:mes:quinn:User"] = "bob_pw",
            ["Saem:Sources:PasswordLogins:mes:quinn:Password"] = "rig-bob-local-only",
        });
        builder.Services.AddSaemMcp(builder.Configuration);
        _mcp = builder.Build();
        _mcp.MapSaemMcp();
        await _mcp.StartAsync();

        var mcpEndpoint = _mcp.Urls.First() + "/mcp";
        _console = await ConsoleServer.StartAsync(builders: "g-plant", liveIssuer: Issuer, ontologyJson: OntologyJson, configure: services =>
            services.Configure<ConsoleLookupOptions>(lookup =>
            {
                lookup.McpEndpoint = mcpEndpoint;
            }));
    }

    public async ValueTask DisposeAsync()
    {
        await _console.DisposeAsync();
        await _mcp.DisposeAsync();
        File.Delete(_mcpOntology);
    }

    [Fact]
    public async Task A_builders_local_graph_is_the_servers_answer_for_them_asked_through_the_console()
    {
        var ct = TestContext.Current.CancellationToken;
        using var pat = _console.Client(await TokenAsync("pat", ct));
        using var quinn = _console.Client(await TokenAsync("quinn", ct));

        using var forPat = await pat.PostAsJsonAsync("api/graph/local", Call, ct);
        using var forQuinn = await quinn.PostAsJsonAsync("api/graph/local", Call, ct);
        var answered = await forPat.Content.ReadFromJsonAsync<JsonElement>(ct);
        var refused = await forQuinn.Content.ReadFromJsonAsync<JsonElement>(ct);

        forPat.EnsureSuccessStatusCode();
        Assert.Equal("answered", answered.GetProperty("kind").GetString());
        Assert.Equal(Canary, Assert.Single(answered.GetProperty("claims").EnumerateArray()).GetProperty("value").GetString());
        // The server answered pat, and knows the console asked: the exchange named it the actor.
        Assert.Equal("pat", answered.GetProperty("answeredFor").GetProperty("subject").GetString());
        Assert.Equal("saem-console", answered.GetProperty("answeredFor").GetProperty("actedBy").GetString());

        Assert.Equal("refused", refused.GetProperty("kind").GetString());
        Assert.DoesNotContain(Canary, refused.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_browser_signs_in_through_the_console_holds_only_a_session_and_its_lookups_are_asked_as_the_person()
    {
        var ct = TestContext.Current.CancellationToken;
        var cookies = new CookieContainer();
        using var browser = _console.Client(handler: new HttpClientHandler { CookieContainer = cookies });

        // The rig's issuer signs pat in without a login page; the browser follows it back to the console and on to /api/me.
        var me = await (await browser.GetAsync("login?returnUrl=/api/me", ct)).Content.ReadFromJsonAsync<Caller>(ct);
        Assert.Equal("pat", me!.Subject);
        Assert.Equal([ConsoleRoles.Builder], me.Roles);
        var held = cookies.GetCookies(_console.Address).Where(c => !c.Expired).ToList();
        Assert.All(held, c => Assert.True(c.HttpOnly));
        Assert.Contains(held, c => c.Name.StartsWith("saem-console", StringComparison.Ordinal));

        using var unmarked = await browser.PostAsJsonAsync("api/graph/local", Call, ct);
        using var marked = new HttpRequestMessage(HttpMethod.Post, "api/graph/local") { Content = JsonContent.Create(Call), Headers = { { ConsoleAuthentication.RequestHeader, "1" } } };
        using var asked = await browser.SendAsync(marked, ct);
        var answered = await asked.Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Equal(HttpStatusCode.BadRequest, unmarked.StatusCode);
        asked.EnsureSuccessStatusCode();
        Assert.Equal(Canary, Assert.Single(answered.GetProperty("claims").EnumerateArray()).GetProperty("value").GetString());
        Assert.Equal("pat", answered.GetProperty("answeredFor").GetProperty("subject").GetString());
        Assert.Equal("saem-console", answered.GetProperty("answeredFor").GetProperty("actedBy").GetString());

        using var logout = new HttpRequestMessage(HttpMethod.Post, "logout") { Headers = { { ConsoleAuthentication.RequestHeader, "1" } } };
        Assert.Equal(HttpStatusCode.NoContent, (await browser.SendAsync(logout, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("api/me", ct)).StatusCode);
    }

    [Fact]
    public async Task A_session_outlives_its_access_token_by_renewing_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var browser = _console.Client(handler: new HttpClientHandler { CookieContainer = new CookieContainer() });
        Assert.Equal("pat", (await (await browser.GetAsync("login?returnUrl=/api/me", ct)).Content.ReadFromJsonAsync<Caller>(ct))!.Subject);

        // The rig's issuer gives twenty-second tokens: past that the session would have ended without a renewal.
        await Task.Delay(TimeSpan.FromSeconds(22), ct);
        using var marked = new HttpRequestMessage(HttpMethod.Post, "api/graph/local") { Content = JsonContent.Create(Call), Headers = { { ConsoleAuthentication.RequestHeader, "1" } } };
        using var asked = await browser.SendAsync(marked, ct);

        asked.EnsureSuccessStatusCode();
        Assert.Equal(Canary, Assert.Single((await asked.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("claims").EnumerateArray()).GetProperty("value").GetString());
    }

    [Fact]
    public async Task Sign_in_returns_only_to_an_address_on_the_console()
    {
        var ct = TestContext.Current.CancellationToken;
        using var browser = _console.Client(handler: new HttpClientHandler { CookieContainer = new CookieContainer() });

        using var landed = await browser.GetAsync("login?returnUrl=//elsewhere.example/steal", ct);

        Assert.Equal(_console.Address.Authority, landed.RequestMessage!.RequestUri!.Authority);
        Assert.Equal("/", landed.RequestMessage.RequestUri.AbsolutePath);
    }

    private static async Task<string> TokenAsync(string person, CancellationToken ct)
    {
        using var http = new HttpClient();
        using var response = await http.PostAsync(Issuer + "/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = person,
            ["client_secret"] = "rig-local-only",
            ["scope"] = "saem-rig",
            ["audience"] = ConsoleServer.Audience,
        }), ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("access_token").GetString()!;
    }
}
