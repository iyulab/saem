using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Saem.Mcp.Tests.Live;

/// <summary>
/// The MCP server against the rig: a person's token from the rig's issuer, presented to the server,
/// and the lookup read from the rig's sources as that person — mes (PostgreSQL, row-level security)
/// and erp (MySQL, views over an access table) deciding, not saem, what each person sees.
/// <para>
/// The source login is each person's interim password login: this shows the token decides
/// whose session the lookup reads through, not that the token reached the source.
/// </para>
/// </summary>
public sealed class McpRigLiveTests : IAsyncLifetime
{
    private const string Canary = "CANARY-7f3a-line2-only";

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

    private static readonly string Issuer = Environment.GetEnvironmentVariable("SAEM_RIG_ISSUER")
        ?? throw new InvalidOperationException("SAEM_RIG_ISSUER names the rig's issuer as the host reaches it (tests/rig/compose.password-login.yaml).");

    private static readonly string Mes = Environment.GetEnvironmentVariable("SAEM_RIG_MES")
        ?? throw new InvalidOperationException("SAEM_RIG_MES names the rig's mes source (tests/rig/compose.password-login.yaml).");

    private static readonly string Erp = Environment.GetEnvironmentVariable("SAEM_RIG_ERP")
        ?? throw new InvalidOperationException("SAEM_RIG_ERP names the rig's erp source (tests/rig/compose.password-login.yaml).");

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
            ["Saem:Sources:MySql:erp"] = Erp,
            ["Saem:Sources:PasswordLogins:erp:alice:User"] = "alice_pw",
            ["Saem:Sources:PasswordLogins:erp:alice:Password"] = "rig-alice-local-only",
            ["Saem:Sources:PasswordLogins:erp:bob:User"] = "bob_pw",
            ["Saem:Sources:PasswordLogins:erp:bob:Password"] = "rig-bob-local-only",
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
        // Nor in saem's log: what a lookup read is answered, never logged.
        Assert.DoesNotContain(_server.Logs.Lines, l => l.Contains(Canary, StringComparison.Ordinal));
    }

    [Fact]
    public async Task One_call_across_both_systems_shows_both_in_its_path_and_stops_where_the_person_cannot_see()
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
        var hops = claim.GetProperty("path").EnumerateArray().ToList();
        Assert.Equal(["WorkOrder", "Item", "Supplier"], [hops[0].GetProperty("from").GetProperty("type").GetString(), hops[0].GetProperty("to").GetProperty("type").GetString(), hops[1].GetProperty("to").GetProperty("type").GetString()]);

        // bob may see WO-2 in mes, but not its item in erp: refused at the hop into erp, naming erp.
        var refusal = forBob.StructuredContent!.Value.GetProperty("refusal");
        Assert.Equal(1, refusal.GetProperty("atHop").GetInt32());
        Assert.Equal("erp", refusal.GetProperty("system").GetString());
        Assert.DoesNotContain(SupplierCanary, forBob.StructuredContent!.Value.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(_server.Logs.Lines, l => l.Contains(SupplierCanary, StringComparison.Ordinal));

        // erp's own audit log records alice's login with the session named after her lookup.
        var lookupId = InteractionId("alice", "WorkOrder");
        var audit = await ContainerTextAsync(RigContainer("erp", "SAEM_RIG_ERP_CONTAINER"), ["cat", "/var/lib/mysql/audit_filter.log"], ct);
        var record = Assert.Single(audit.Split("<AUDIT_RECORD>"), r => r.Contains($"<VALUE>saem/{lookupId}</VALUE>", StringComparison.Ordinal) && r.Contains("<NAME>Connect</NAME>", StringComparison.Ordinal));
        Assert.Contains("<USER>alice_pw</USER>", record, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_source_logs_each_lookup_as_the_login_of_the_person_saem_logged_it_for()
    {
        var ct = TestContext.Current.CancellationToken;
        var call = new Dictionary<string, object?>
        {
            ["start"] = new Dictionary<string, object?> { ["type"] = "WorkOrder", ["key"] = "WO-1" },
            ["path"] = Array.Empty<string>(),
            ["read"] = new[] { "quantity" },
        };

        foreach (var person in new[] { "alice", "bob" })
        {
            await using var client = await _server.ClientAsync(await TokenAsync(person, ct), ct);
            await client.CallToolAsync("lookup", call, cancellationToken: ct);
        }

        var sourceLog = await MesLogAsync(ct);
        foreach (var person in new[] { "alice", "bob" })
        {
            // saem's side: the lookup, its id, and whom it ran for.
            var lookupId = InteractionId(person, "WorkOrder");

            // The source's side: the login it authorised for a session named after that lookup is the person's own.
            var logins = sourceLog.Where(l => l.Contains("connection authorized:", StringComparison.Ordinal) && l.Contains($" application_name=saem/{lookupId} ", StringComparison.Ordinal)).ToList();
            var login = Assert.Single(logins);
            Assert.Contains($"connection authorized: user={person}_pw ", login, StringComparison.Ordinal);
        }
    }

    // saem's side of a lookup: its interaction record (`interaction <id> <outcome> <json>`), whose id names the lookup.
    private string InteractionId(string person, string startType) =>
        Assert.Single(_server.Logs.Lines, l => l.StartsWith("interaction ", StringComparison.Ordinal)
            && l.Contains($"\"subject\":\"{person}\"", StringComparison.Ordinal)
            && l.Contains($"\"startType\":\"{startType}\"", StringComparison.Ordinal)).Split(' ')[1];

    // A rig's containers are named after its compose project (`<project>-<service>-1`), and its network after it too
    // (`<project>_default`): a rig started under another project name (to run beside one that is up) is reached through
    // the network name the other live tests already take, and an environment variable names the container outright.
    private static string RigContainer(string service, string variable) =>
        Environment.GetEnvironmentVariable(variable)
        ?? $"{(Environment.GetEnvironmentVariable("SAEM_RIG_NETWORK") ?? "saem-rig_default").Replace("_default", string.Empty, StringComparison.Ordinal)}-{service}-1";

    // The rig's mes container log, where PostgreSQL writes its connection log.
    private static async Task<string[]> MesLogAsync(CancellationToken ct) =>
        (await DockerAsync(["logs", RigContainer("mes", "SAEM_RIG_MES_CONTAINER")], ct)).Split('\n');

    private static Task<string> ContainerTextAsync(string container, string[] command, CancellationToken ct) =>
        DockerAsync(["exec", container, .. command], ct);

    // docker's output, both streams: a container's log is written to either.
    private static async Task<string> DockerAsync(string[] arguments, CancellationToken ct)
    {
        using var docker = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("docker", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var stdout = docker.StandardOutput.ReadToEndAsync(ct);
        var stderr = docker.StandardError.ReadToEndAsync(ct);
        await docker.WaitForExitAsync(ct);
        Assert.True(docker.ExitCode == 0, $"docker {string.Join(' ', arguments)} failed: {await stderr}");
        return (await stdout) + (await stderr);
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
