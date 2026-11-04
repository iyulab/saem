using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Saem.Mcp.Tests.Live;

/// <summary>
/// The MCP server against the rig with the person's token reaching erp (MySQL): the server exchanges the
/// token it was called with for one meant for erp, and erp's OpenID Connect login checks it and logs the
/// session in as the account the token's subject was created for. Mes (PostgreSQL) is still the interim
/// password login — its driver cannot present a token yet.
/// </summary>
public sealed class McpRigErpTokenLiveTests : IAsyncLifetime
{
    private const string SupplierCanary = "CANARY-91c4-supplier-two";

    private const string OntologyJson = """
        [
          { "kind": "entityType", "name": "WorkOrder", "system": "mes", "table": "work_orders", "key": "wo_no",
            "fields": { "note": "note", "quantity": "qty", "item": "item_code" } },
          { "kind": "entityType", "name": "Item", "system": "erp", "table": "visible_items", "key": "item_code",
            "fields": { "name": "item_name", "supplier": "supplier_id" } },
          { "kind": "entityType", "name": "Supplier", "system": "erp", "table": "visible_suppliers", "key": "supplier_id",
            "fields": { "name": "supplier_name" } },
          { "kind": "relation", "name": "orders_item", "from": "WorkOrder", "via": "item", "to": "Item" },
          { "kind": "relation", "name": "supplied_by", "from": "Item", "via": "supplier", "to": "Supplier" }
        ]
        """;

    private static readonly string Issuer = Required("SAEM_RIG_ISSUER");

    private static readonly string Mes = Required("SAEM_RIG_MES");

    private static readonly string Erp = Required("SAEM_RIG_ERP");

    private TestServer _server = null!;

    private static string Required(string variable) => Environment.GetEnvironmentVariable(variable)
        ?? throw new InvalidOperationException($"{variable} names one of the rig's parts as the host reaches it (tests/rig/compose.password-login.yaml).");

    public async ValueTask InitializeAsync()
    {
        _server = await TestServer.StartAsync(
            OntologyJson,
            new Dictionary<string, string?>
            {
                ["Saem:Identity:Authority"] = Issuer,
                ["Saem:Identity:Audience"] = "saem",
                ["Saem:Identity:RequireHttpsMetadata"] = "false",
                ["Saem:Sources:Postgres:mes"] = Mes,
                ["Saem:Sources:PasswordLogins:mes:alice:User"] = "alice_pw",
                ["Saem:Sources:PasswordLogins:mes:alice:Password"] = "rig-alice-local-only",
                ["Saem:Sources:PasswordLogins:mes:bob:User"] = "bob_pw",
                ["Saem:Sources:PasswordLogins:mes:bob:Password"] = "rig-bob-local-only",
                ["Saem:Sources:MySqlToken:erp:ConnectionString"] = Erp,
                ["Saem:Sources:MySqlToken:erp:Audience"] = "saem-rig-erp",
                ["Saem:Sources:TokenExchange:TokenEndpoint"] = Issuer + "/token",
                ["Saem:Sources:TokenExchange:ClientId"] = "saem",
                ["Saem:Sources:TokenExchange:ClientSecret"] = "rig-local-only",
            },
            services => services.AddHttpClient(nameof(TokenExchange)).AddHttpMessageHandler(() => new IssuerHostHandler()));
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task The_token_reaches_erp_so_alice_reads_the_supplier_and_bob_is_refused_there()
    {
        var ct = TestContext.Current.CancellationToken;
        var supplierOf = new Dictionary<string, object?>
        {
            ["start"] = new Dictionary<string, object?> { ["type"] = "WorkOrder", ["key"] = "WO-2" },
            ["path"] = new[] { "orders_item", "supplied_by" },
            ["read"] = new[] { "name" },
        };

        await using var alice = await _server.ClientAsync(await TokenAsync("alice", ct), ct);
        var forAlice = await alice.CallToolAsync("lookup", supplierOf, cancellationToken: ct);
        await using var bob = await _server.ClientAsync(await TokenAsync("bob", ct), ct);
        var forBob = await bob.CallToolAsync("lookup", supplierOf, cancellationToken: ct);

        var claim = Assert.Single(forAlice.StructuredContent!.Value.GetProperty("claims").EnumerateArray());
        Assert.Equal(SupplierCanary, claim.GetProperty("value").GetString());
        Assert.Equal("erp", Assert.Single(claim.GetProperty("sources").EnumerateArray()).GetProperty("system").GetString());

        // bob may see WO-2 in mes but not its item in erp: erp's own rules, applied to the account his token names.
        var refusal = forBob.StructuredContent!.Value.GetProperty("refusal");
        Assert.Equal(1, refusal.GetProperty("atHop").GetInt32());
        Assert.Equal("erp", refusal.GetProperty("system").GetString());
        Assert.DoesNotContain(SupplierCanary, forBob.StructuredContent!.Value.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(_server.Logs.Lines, l => l.Contains(SupplierCanary, StringComparison.Ordinal));
    }

    /// <summary>The person's token meant for saem, as the rig's issuer hands it out.</summary>
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

    /// <summary>
    /// The rig's issuer names itself in a token by the host the request reached it at, and erp trusts it under the name the
    /// rig's network gives it — so the exchange asks for that name, though the test reaches the issuer on the loopback.
    /// </summary>
    private sealed class IssuerHostHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Host = "issuer:8080";
            return base.SendAsync(request, cancellationToken);
        }
    }
}
