using System.Text.Json;
using Saem.Connectors.Postgres;
using Saem.Materialization;
using Saem.Ontology;
using Saem.Query;
using Xunit;

namespace Saem.Mcp.Tests.LiveForms;

/// <summary>
/// H2b's question on the rig (D-22): which work orders make item I-100 — a relation walked backwards, which
/// only the edges materialized for a group answer. Each group's edges are built from what the mes source
/// shows that group's account (g_plant every line, g_office line 1) into the group's own store; the server
/// reads a person's groups from the issuer's token. pat is in g-plant and reads mes as alice does; quinn is in
/// g-plant too but reads mes as bob does; olive is in g-office and reads it as bob does; nobody is in no group.
/// The line-2 work order is the canary.
/// </summary>
public sealed class GroupLookupLiveTests : IAsyncLifetime
{
    private const string BaseIri = "https://rig.saem.test/ontology/";

    private const string OntologyJson = """
        [
          { "kind": "entityType", "name": "WorkOrder", "system": "mes", "table": "work_orders", "key": "wo_no",
            "fields": { "item": "item_code", "quantity": "qty", "note": "note" } },
          { "kind": "entityType", "name": "Item", "system": "erp", "table": "items", "key": "item_code", "fields": { } },
          { "kind": "relation", "name": "orders_item", "from": "WorkOrder", "via": "item", "to": "Item", "inverse": "ordered_by" }
        ]
        """;

    private static readonly string Mes = Environment.GetEnvironmentVariable("SAEM_RIG_MES")
        ?? throw new InvalidOperationException("SAEM_RIG_MES is set by the rig's password-login profile (tests/rig/compose.password-login.yaml).");

    private static readonly Dictionary<string, object?> WhoMakesI100 = new()
    {
        ["start"] = new Dictionary<string, object?> { ["type"] = "Item", ["key"] = "I-100" },
        ["path"] = new[] { "ordered_by" },
        ["read"] = new[] { "quantity" },
    };

    private static readonly Dictionary<string, object?> WorkOrder3 = new()
    {
        ["start"] = new Dictionary<string, object?> { ["type"] = "WorkOrder", ["key"] = "WO-3" },
        ["path"] = Array.Empty<string>(),
        ["read"] = new[] { "quantity" },
    };

    private GroupStoreRig _plant = null!;
    private GroupStoreRig _office = null!;
    private TestServer _server = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _plant = await GroupStoreRig.StartAsync("g-plant", ct);
        _office = await GroupStoreRig.StartAsync("g-office", ct);

        // Each group's pass, as the group's account.
        var file = Path.GetTempFileName();
        await File.WriteAllTextAsync(file, OntologyJson, ct);
        var ontology = await new FileOntologyCatalog(file).GetAsync(ct);
        File.Delete(file);
        var materializer = new EdgeMaterializer(ontology, new EntityIris(new Uri(BaseIri)));
        var mes = new PostgresSource(Mes);
        await new FormbaseEdgeStore(_plant.Formbase).ReplaceAsync(materializer.MaterializeAsync(As(mes, "g_plant_pw", "rig-plant-local-only"), ct), ct);
        await new FormbaseEdgeStore(_office.Formbase).ReplaceAsync(materializer.MaterializeAsync(As(mes, "g_office_pw", "rig-office-local-only"), ct), ct);

        var issuer = Environment.GetEnvironmentVariable("SAEM_RIG_ISSUER")!;
        _server = await TestServer.StartAsync(OntologyJson, new Dictionary<string, string?>
        {
            ["Saem:Identity:Authority"] = issuer,
            ["Saem:Identity:Audience"] = "saem",
            ["Saem:Identity:RequireHttpsMetadata"] = "false",
            ["Saem:Sources:Postgres:mes"] = Mes,
            ["Saem:Sources:PasswordLogins:mes:pat:User"] = "alice_pw",
            ["Saem:Sources:PasswordLogins:mes:pat:Password"] = "rig-alice-local-only",
            ["Saem:Sources:PasswordLogins:mes:quinn:User"] = "bob_pw",
            ["Saem:Sources:PasswordLogins:mes:quinn:Password"] = "rig-bob-local-only",
            ["Saem:Sources:PasswordLogins:mes:olive:User"] = "bob_pw",
            ["Saem:Sources:PasswordLogins:mes:olive:Password"] = "rig-bob-local-only",
            ["Saem:Sources:PasswordLogins:mes:nobody:User"] = "alice_pw",
            ["Saem:Sources:PasswordLogins:mes:nobody:Password"] = "rig-alice-local-only",
            ["Saem:Materialization:BaseIri"] = BaseIri,
            ["Saem:Materialization:Groups:g-plant:Url"] = _plant.Formbase.BaseAddress!.AbsoluteUri,
            ["Saem:Materialization:Groups:g-office:Url"] = _office.Formbase.BaseAddress!.AbsoluteUri,
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        await _plant.DisposeAsync();
        await _office.DisposeAsync();
    }

    private static PersonSessions As(PostgresSource mes, string account, string password) =>
        new(new Dictionary<string, SourceOpener> { ["mes"] = mes.As(account, _ => ValueTask.FromResult(password)) });

    private async Task<JsonElement> AskAsync(string person, CancellationToken ct)
    {
        await using var client = await _server.ClientAsync(await FormsRig.TokenAsync(person, ct), ct);
        var result = await client.CallToolAsync("lookup", WhoMakesI100, cancellationToken: ct);
        Assert.NotEqual(true, result.IsError);
        // Every hop walked backwards says when its partition's edges were read.
        if (result.StructuredContent!.Value.TryGetProperty("claims", out var claims))
        {
            Assert.All(claims.EnumerateArray(), c => Assert.True(c.GetProperty("path")[0].TryGetProperty("partitionAsOf", out _)));
        }
        return result.StructuredContent!.Value;
    }

    private static IEnumerable<(string WorkOrder, int Quantity, string? Partition)> Claims(JsonElement answer) =>
        answer.GetProperty("claims").EnumerateArray().Select(c => (
            c.GetProperty("entity").GetProperty("key").GetString()!,
            c.GetProperty("value").GetInt32(),
            c.GetProperty("path")[0].GetProperty("partition").GetString()));

    /// <summary>The same question, two people of different groups: each is answered from their own group's partition only.</summary>
    [Fact]
    public async Task Two_people_of_different_groups_are_answered_each_from_their_groups_partition()
    {
        var ct = TestContext.Current.CancellationToken;

        var forPat = await AskAsync("pat", ct);
        var forOlive = await AskAsync("olive", ct);

        Assert.Equal([("WO-1", 50, "g-plant"), ("WO-3", 10, "g-plant")], Claims(forPat));
        Assert.Equal([("WO-1", 50, "g-office")], Claims(forOlive));
        Assert.DoesNotContain("WO-3", forOlive.GetRawText(), StringComparison.Ordinal);
        var source = forPat.GetProperty("claims")[1].GetProperty("sources")[0];
        Assert.Equal(("mes", "work_orders", "WO-3"), (source.GetProperty("system").GetString(), source.GetProperty("table").GetString(), source.GetProperty("record").GetString()));
    }

    /// <summary>
    /// R-5: the edges are the group's, the answer is the person's. quinn's group holds the canary's edge, but the
    /// source does not show quinn the canary's row, so quinn's answer is what pat's would be minus that row.
    /// </summary>
    [Fact]
    public async Task Within_one_group_each_person_is_answered_only_what_the_source_shows_them()
    {
        var ct = TestContext.Current.CancellationToken;

        var forQuinn = await AskAsync("quinn", ct);

        Assert.Equal([("WO-1", 50, "g-plant")], Claims(forQuinn));
        Assert.DoesNotContain("WO-3", forQuinn.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>T-41: someone in no group the server keeps edges for sees nothing materialized — refused for that reason.</summary>
    [Fact]
    public async Task Someone_in_no_group_is_refused_the_backward_step_and_still_answered_forwards()
    {
        var ct = TestContext.Current.CancellationToken;

        var backwards = await AskAsync("nobody", ct);
        await using var client = await _server.ClientAsync(await FormsRig.TokenAsync("nobody", ct), ct);
        var forwards = await client.CallToolAsync("lookup", WorkOrder3, cancellationToken: ct);

        Assert.Equal("refused", backwards.GetProperty("kind").GetString());
        Assert.Equal("noMaterializedPartition", backwards.GetProperty("refusal").GetProperty("reason").GetString());
        Assert.Equal("answered", forwards.StructuredContent!.Value.GetProperty("kind").GetString());
    }
}
