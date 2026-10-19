using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Saem.Mcp.Tests.Live;

/// <summary>
/// The MCP server against the rig: a person's token from the rig's issuer, presented to the server,
/// and the lookup read from the rig's mes source as that person — the source's row-level security,
/// not saem, deciding that alice sees WO-3's note and bob does not.
/// <para>
/// The source login is each person's interim password login: this shows the token decides
/// whose session the lookup reads through, not that the token reached the source.
/// </para>
/// </summary>
public sealed class McpRigLiveTests : IAsyncLifetime
{
    private const string Canary = "CANARY-7f3a-line2-only";

    private const string OntologyJson = """
        [
          { "kind": "entityType", "name": "WorkOrder", "system": "mes", "table": "work_orders", "key": "wo_no",
            "fields": { "note": "note", "quantity": "qty" } }
        ]
        """;

    private static readonly string Issuer = Environment.GetEnvironmentVariable("SAEM_RIG_ISSUER")
        ?? throw new InvalidOperationException("SAEM_RIG_ISSUER names the rig's issuer as the host reaches it (tests/rig/compose.password-login.yaml).");

    private static readonly string Mes = Environment.GetEnvironmentVariable("SAEM_RIG_MES")
        ?? throw new InvalidOperationException("SAEM_RIG_MES names the rig's mes source (tests/rig/compose.password-login.yaml).");

    private TestServer _server = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await TestServer.StartAsync(OntologyJson, new Dictionary<string, string?>
        {
            ["Saem:Identity:Authority"] = Issuer,
            ["Saem:Identity:Audience"] = "saem",
            ["Saem:Identity:RequireHttpsMetadata"] = "false",
            ["Saem:Sources:Postgres:mes"] = Mes,
            ["Saem:Sources:PasswordLogins:mes:alice:User"] = "alice_pw",
            ["Saem:Sources:PasswordLogins:mes:alice:Password"] = "rig-alice-local-only",
            ["Saem:Sources:PasswordLogins:mes:bob:User"] = "bob_pw",
            ["Saem:Sources:PasswordLogins:mes:bob:Password"] = "rig-bob-local-only",
        });
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task One_call_answers_alice_and_refuses_bob_as_the_source_decides()
    {
        var ct = TestContext.Current.CancellationToken;
        var call = new Dictionary<string, object?>
        {
            ["start"] = new Dictionary<string, object?> { ["type"] = "WorkOrder", ["key"] = "WO-3" },
            ["path"] = Array.Empty<string>(),
            ["read"] = new[] { "note" },
        };

        await using var alice = await _server.ClientAsync(await TokenAsync("alice", ct), ct);
        var forAlice = await alice.CallToolAsync("lookup", call, cancellationToken: ct);
        await using var bob = await _server.ClientAsync(await TokenAsync("bob", ct), ct);
        var forBob = await bob.CallToolAsync("lookup", call, cancellationToken: ct);

        var claim = Assert.Single(forAlice.StructuredContent!.Value.GetProperty("claims").EnumerateArray());
        Assert.Equal(Canary, claim.GetProperty("value").GetString());
        Assert.Equal("mes", Assert.Single(claim.GetProperty("sources").EnumerateArray()).GetProperty("system").GetString());
        var refused = forBob.StructuredContent!.Value;
        Assert.Equal("refused", refused.GetProperty("kind").GetString());
        Assert.DoesNotContain(Canary, refused.GetRawText(), StringComparison.Ordinal);
    }

    // The rig issuer answers client credentials with a token whose sub is the client id and whose aud is
    // the asked audience — standing in for the token an agent would hold for the person.
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
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return body.GetProperty("access_token").GetString()!;
    }
}
