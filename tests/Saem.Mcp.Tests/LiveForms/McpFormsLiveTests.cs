using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Saem.Mcp.Tests.LiveForms;

/// <summary>
/// The MCP server against the rig's forms source (tests/rig/compose.morphdb.yaml): the person's token,
/// presented to saem, is exchanged at the issuer for one meant for MorphDB, and MorphDB's row-level
/// security — evaluated as that person — decides what the lookup answers. Unlike the password-login
/// sources, here the person's own token reaches the source.
/// </summary>
public sealed class McpFormsLiveTests : IAsyncLifetime
{
    private const string Canary = "CANARY-5d2e-inspection-alice";

    private static readonly string Issuer = Environment.GetEnvironmentVariable("SAEM_RIG_ISSUER")
        ?? throw new InvalidOperationException("SAEM_RIG_ISSUER names the rig's issuer as the host reaches it (tests/rig/compose.morphdb.yaml).");

    private static readonly string Forms = Environment.GetEnvironmentVariable("SAEM_RIG_FORMS")
        ?? throw new InvalidOperationException("SAEM_RIG_FORMS names the rig's forms source (tests/rig/compose.morphdb.yaml).");

    private static readonly string MasterSecret = Environment.GetEnvironmentVariable("SAEM_RIG_FORMS_MASTER_SECRET")
        ?? throw new InvalidOperationException("SAEM_RIG_FORMS_MASTER_SECRET is the forms source's master secret (tests/rig/compose.morphdb.yaml).");

    private static readonly string[] ReadResult = ["result"];

    private readonly string _table = $"inspection_{Guid.NewGuid():N}"[..24];
    private Guid _project;
    private TestServer _server = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        using var master = Master();
        var project = await master.PostAsJsonAsync("api/projects", new { name = $"saem-mcp-{Guid.NewGuid():N}"[..24] }, ct);
        project.EnsureSuccessStatusCode();
        _project = (await project.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
        master.DefaultRequestHeaders.Add("X-Project-Id", _project.ToString());

        (await master.PostAsJsonAsync("api/schema/tables", new
        {
            name = _table,
            columns = new[]
            {
                new { name = "fb_doc_id", type = "text", nullable = false },
                new { name = "inspector", type = "text", nullable = false },
                new { name = "result", type = "text", nullable = false },
            },
        }, ct)).EnsureSuccessStatusCode();
        (await master.PostAsJsonAsync($"api/data/{_table}",
            new Dictionary<string, string> { ["fb_doc_id"] = "doc-1", ["inspector"] = "alice", ["result"] = Canary }, ct)).EnsureSuccessStatusCode();
        (await master.PostAsJsonAsync("api/security/policies", new
        {
            name = "own_inspections",
            tableName = _table,
            policyType = "Select",
            expression = "inspector = {{user_id}}",
        }, ct)).EnsureSuccessStatusCode();

        var ontology = $$"""
            [
              { "kind": "entityType", "name": "Inspection", "system": "forms", "table": "{{_table}}", "key": "fb_doc_id",
                "fields": { "result": "result" } }
            ]
            """;
        _server = await TestServer.StartAsync(ontology, new Dictionary<string, string?>
        {
            ["Saem:Identity:Authority"] = Issuer,
            ["Saem:Identity:Audience"] = "saem",
            ["Saem:Identity:RequireHttpsMetadata"] = "false",
            ["Saem:Sources:MorphDb:forms:Url"] = Forms,
            ["Saem:Sources:MorphDb:forms:ProjectId"] = _project.ToString(),
            ["Saem:Sources:MorphDb:forms:Audience"] = "morphdb",
            ["Saem:Sources:TokenExchange:TokenEndpoint"] = Issuer + "/token",
            ["Saem:Sources:TokenExchange:ClientId"] = "saem",
            ["Saem:Sources:TokenExchange:ClientSecret"] = "rig-local-only",
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        using var master = Master();
        await master.DeleteAsync($"api/projects/{_project}");
    }

    private static HttpClient Master()
    {
        var master = new HttpClient { BaseAddress = new Uri(Forms) };
        master.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MasterSecret);
        return master;
    }

    [Fact]
    public async Task The_persons_own_token_reaches_the_source_and_the_source_decides()
    {
        var ct = TestContext.Current.CancellationToken;
        var call = new Dictionary<string, object?>
        {
            ["start"] = new Dictionary<string, object?> { ["type"] = "Inspection", ["key"] = "doc-1" },
            ["path"] = Array.Empty<string>(),
            ["read"] = new[] { "result" },
        };

        await using var alice = await _server.ClientAsync(await TokenAsync("alice", ct), ct);
        var forAlice = await alice.CallToolAsync("lookup", call, cancellationToken: ct);
        await using var bob = await _server.ClientAsync(await TokenAsync("bob", ct), ct);
        var forBob = await bob.CallToolAsync("lookup", call, cancellationToken: ct);

        var claim = Assert.Single(forAlice.StructuredContent!.Value.GetProperty("claims").EnumerateArray());
        Assert.Equal(Canary, claim.GetProperty("value").GetString());
        var source = Assert.Single(claim.GetProperty("sources").EnumerateArray());
        Assert.Equal("forms", source.GetProperty("system").GetString());
        Assert.Equal("doc-1", source.GetProperty("record").GetString());

        Assert.Equal("refused", forBob.StructuredContent!.Value.GetProperty("kind").GetString());
        Assert.DoesNotContain(Canary, forBob.StructuredContent!.Value.GetRawText(), StringComparison.Ordinal);
        // Neither the answer nor a token is logged.
        Assert.DoesNotContain(_server.Logs.Lines, l => l.Contains(Canary, StringComparison.Ordinal) || l.Contains("eyJ", StringComparison.Ordinal));
    }

    /// <summary>
    /// An agent acting for alice holds a token naming her as its subject and itself in <c>act</c>. saem
    /// answers as alice, says an agent asked, and the token it exchanges for MorphDB keeps the <c>act</c> —
    /// so MorphDB's own audit trail tells the agent's read from alice's.
    /// </summary>
    [Fact]
    public async Task An_agents_read_for_a_person_is_the_agents_in_the_sources_audit_trail()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var agent = await _server.ClientAsync(await TokenAsync("agent-for-alice", ct), ct);

        var answer = (await agent.CallToolAsync("lookup", new Dictionary<string, object?>
        {
            ["start"] = new Dictionary<string, object?> { ["type"] = "Inspection", ["key"] = "doc-1" },
            ["path"] = Array.Empty<string>(),
            ["read"] = ReadResult,
        }, cancellationToken: ct)).StructuredContent!.Value;

        Assert.Equal(Canary, Assert.Single(answer.GetProperty("claims").EnumerateArray()).GetProperty("value").GetString());
        Assert.Equal(("alice", "agent-7"), (
            answer.GetProperty("answeredFor").GetProperty("subject").GetString(),
            answer.GetProperty("answeredFor").GetProperty("actedBy").GetString()));

        using var master = Master();
        for (var attempt = 0; ; attempt++)
        {
            var page = await master.GetFromJsonAsync<JsonElement>($"api/projects/{_project}/audit/logs?actorId=alice", ct);
            var read = page.GetProperty("items").EnumerateArray().FirstOrDefault(e => e.GetProperty("httpMethod").GetString() == "GET");
            if (read.ValueKind == JsonValueKind.Object)
            {
                if (!read.TryGetProperty("actedBy", out var actedBy))
                {
                    Assert.Skip("This MorphDB records no actedBy (before the release that adds it); the rig's image is SAEM_RIG_MORPHDB_IMAGE.");
                }

                Assert.Equal("agent-7", actedBy.GetString());
                return;
            }

            Assert.True(attempt < 50, "MorphDB's audit row for alice's read did not arrive.");
            await Task.Delay(200, ct);
        }
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
            ["audience"] = "saem",
        }), ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("access_token").GetString()!;
    }
}
