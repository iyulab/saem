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
/// A confirmed draft is the catalog a real MCP server answers by (saem D-88 C4): the console and the server read one
/// catalog file, a builder signed in at the rig's issuer confirms a draft that declares a type, and the very next
/// lookup — a person's, through the console, to mes — is answered where it was refused before.
/// </summary>
public sealed class CatalogConfirmLiveTests : IAsyncLifetime
{
    private const string Canary = "CANARY-7f3a-line2-only";

    private const string Draft = """
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

    private readonly string _catalog = Path.GetTempFileName();
    private readonly string _draft = Path.GetTempFileName();
    private readonly string _confirmations = Path.Combine(Path.GetTempPath(), $"confirmations-{Guid.NewGuid():N}.jsonl");
    private WebApplication _mcp = null!;
    private ConsoleServer _console = null!;

    public async ValueTask InitializeAsync()
    {
        await File.WriteAllTextAsync(_catalog, "[]");
        await File.WriteAllTextAsync(_draft, Draft);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Saem:Ontology"] = _catalog,
            ["Saem:Identity:Authority"] = Issuer,
            ["Saem:Identity:Audience"] = "saem",
            ["Saem:Identity:RequireHttpsMetadata"] = "false",
            ["Saem:Sources:Postgres:mes"] = Mes,
            ["Saem:Sources:PasswordLogins:mes:pat:User"] = "alice_pw",
            ["Saem:Sources:PasswordLogins:mes:pat:Password"] = "rig-alice-local-only",
        });
        builder.Services.AddSaemMcp(builder.Configuration);
        _mcp = builder.Build();
        _mcp.MapSaemMcp();
        await _mcp.StartAsync();

        var mcpEndpoint = _mcp.Urls.First() + "/mcp";
        _console = await ConsoleServer.StartAsync(
            builders: "g-plant",
            liveIssuer: Issuer,
            settings: new Dictionary<string, string?>
            {
                ["Saem:Console:Ontology"] = _catalog,
                ["Saem:Console:Draft"] = _draft,
                ["Saem:Console:Confirmations"] = _confirmations,
            },
            configure: services => services.Configure<ConsoleLookupOptions>(lookup => lookup.McpEndpoint = mcpEndpoint));
    }

    public async ValueTask DisposeAsync()
    {
        await _console.DisposeAsync();
        await _mcp.DisposeAsync();
        File.Delete(_catalog);
        File.Delete(_draft);
        File.Delete(_confirmations);
    }

    [Fact]
    public async Task A_confirmed_draft_is_what_the_server_answers_by_from_the_next_lookup()
    {
        var ct = TestContext.Current.CancellationToken;
        using var pat = _console.Client(await TokenAsync("pat", ct));

        using var before = await pat.PostAsJsonAsync("api/graph/local", Call, ct);
        Assert.DoesNotContain(Canary, await before.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        var view = await pat.GetFromJsonAsync<JsonElement>("api/catalog/draft", ct);
        Assert.Equal(0, view.GetProperty("breaking").GetInt32());
        using var confirmed = await pat.PostAsJsonAsync(
            "api/catalog/confirm",
            new { @base = view.GetProperty("inForce").GetString(), draft = view.GetProperty("draft").GetString() },
            ct);
        confirmed.EnsureSuccessStatusCode();

        using var after = await pat.PostAsJsonAsync("api/graph/local", Call, ct);
        after.EnsureSuccessStatusCode();
        var answered = await after.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("answered", answered.GetProperty("kind").GetString());
        Assert.Equal(Canary, Assert.Single(answered.GetProperty("claims").EnumerateArray()).GetProperty("value").GetString());
        Assert.Equal(view.GetProperty("draft").GetString(), answered.GetProperty("catalogVersion").GetString());
        Assert.Equal("pat", JsonDocument.Parse(Assert.Single(await File.ReadAllLinesAsync(_confirmations, ct))).RootElement.GetProperty("by").GetString());
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
