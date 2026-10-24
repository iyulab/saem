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
/// The line-2 work order is the canary. An identity holds work orders of one item to be one thing (a mechanism
/// check, by a field the rig has): g_plant sees WO-1 and WO-3 make I-100, g_office sees one order of each item.
/// </summary>
public sealed class GroupLookupLiveTests : IAsyncLifetime
{
    private const string BaseIri = "https://rig.saem.test/ontology/";

    private const string OntologyJson = """
        [
          { "kind": "entityType", "name": "WorkOrder", "system": "mes", "table": "work_orders", "key": "wo_no",
            "fields": { "item": "item_code", "quantity": "qty", "note": "note" } },
          { "kind": "entityType", "name": "Item", "system": "erp", "table": "items", "key": "item_code", "fields": { } },
          { "kind": "relation", "name": "orders_item", "from": "WorkOrder", "via": "item", "to": "Item", "inverse": "ordered_by" },
          { "kind": "identity", "name": "same_item_order", "first": "WorkOrder", "second": "WorkOrder", "decision": "automatic",
            "match": [ { "first": "item", "second": "item" } ] }
        ]
        """;

    private static readonly string Mes = Environment.GetEnvironmentVariable("SAEM_RIG_MES")
        ?? throw new InvalidOperationException("SAEM_RIG_MES is set by the rig's password-login profile (tests/rig/compose.password-login.yaml).");

    private static readonly string[] Quantity = ["quantity"];

    private static readonly string[] SameItemOrder = ["same_item_order"];

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
        var matcher = new IdentityMatcher(ontology, new EntityIris(new Uri(BaseIri)));
        await new FormbaseIdentityStore(_plant.Formbase).ReplaceAsync(ontology, matcher.MatchAsync(As(mes, "g_plant_pw", "rig-plant-local-only"), ct), ct);
        await new FormbaseIdentityStore(_office.Formbase).ReplaceAsync(ontology, matcher.MatchAsync(As(mes, "g_office_pw", "rig-office-local-only"), ct), ct);

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

    /// <summary>
    /// D-46 · D-80 ①: quinn's group led to the canary's row, which quinn's own read does not show — the partition
    /// is reported blocked, one bit with no key or count; pat reads every row it led to, so pat's is not.
    /// </summary>
    [Fact]
    public async Task A_partition_that_led_to_a_row_the_person_cannot_see_is_reported_blocked_and_says_no_more()
    {
        var ct = TestContext.Current.CancellationToken;

        var forQuinn = await AskAsync("quinn", ct);
        var forPat = await AskAsync("pat", ct);

        var quinns = Assert.Single(forQuinn.GetProperty("partitions").EnumerateArray());
        Assert.Equal(("g-plant", true, false), (quinns.GetProperty("name").GetString(), quinns.GetProperty("blocked").GetBoolean(), quinns.GetProperty("incomplete").GetBoolean()));
        Assert.True(quinns.TryGetProperty("asOf", out var asOf) && asOf.ValueKind == JsonValueKind.String);
        Assert.DoesNotContain("WO-3", quinns.GetRawText(), StringComparison.Ordinal);
        Assert.False(Assert.Single(forPat.GetProperty("partitions").EnumerateArray()).GetProperty("blocked").GetBoolean());
        Assert.Equal(forPat.GetProperty("catalogVersion").GetString(), forQuinn.GetProperty("catalogVersion").GetString());
    }

    /// <summary>D-80 ④ on the rig: work orders named by the item they make — pat sees two and is offered both; quinn sees one and is walked from it.</summary>
    [Fact]
    public async Task A_start_by_a_value_offers_the_rows_the_person_sees_or_walks_from_the_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var byItem = new Dictionary<string, object?>
        {
            ["start"] = new Dictionary<string, object?> { ["type"] = "WorkOrder", ["where"] = new Dictionary<string, object?> { ["field"] = "item", ["value"] = "I-100" } },
            ["path"] = Array.Empty<string>(),
            ["read"] = Quantity,
        };

        await using var pat = await _server.ClientAsync(await FormsRig.TokenAsync("pat", ct), ct);
        await using var quinn = await _server.ClientAsync(await FormsRig.TokenAsync("quinn", ct), ct);
        var forPat = (await pat.CallToolAsync("lookup", byItem, cancellationToken: ct)).StructuredContent!.Value;
        var forQuinn = (await quinn.CallToolAsync("lookup", byItem, cancellationToken: ct)).StructuredContent!.Value;

        Assert.Equal("candidates", forPat.GetProperty("kind").GetString());
        Assert.Equal(["WO-1", "WO-3"], forPat.GetProperty("entities").EnumerateArray().Select(e => e.GetProperty("entity").GetProperty("key").GetString()));
        Assert.Equal("answered", forQuinn.GetProperty("kind").GetString());
        Assert.Equal("WO-1", forQuinn.GetProperty("claims")[0].GetProperty("entity").GetProperty("key").GetString());
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

    private async Task<JsonElement> SameAsWorkOrder1Async(string person, CancellationToken ct)
    {
        await using var client = await _server.ClientAsync(await FormsRig.TokenAsync(person, ct), ct);
        var result = await client.CallToolAsync("lookup", new Dictionary<string, object?>
        {
            ["start"] = new Dictionary<string, object?> { ["type"] = "WorkOrder", ["key"] = "WO-1" },
            ["path"] = SameItemOrder,
            ["read"] = Quantity,
        }, cancellationToken: ct);
        Assert.NotEqual(true, result.IsError);
        return result.StructuredContent!.Value;
    }

    /// <summary>
    /// D-86 on the rig: crossing an identity answers the entities the person's group holds the same thing, read as the
    /// person, the hop naming the identity and who settled it. quinn's group holds the canary as one with WO-1, but
    /// quinn's own read does not show it — refused, the partition blocked; olive's group holds no such thing.
    /// </summary>
    [Fact]
    public async Task Crossing_an_identity_answers_what_the_persons_group_holds_the_same_and_the_source_shows_them()
    {
        var ct = TestContext.Current.CancellationToken;

        var forPat = await SameAsWorkOrder1Async("pat", ct);
        var forQuinn = await SameAsWorkOrder1Async("quinn", ct);
        var forOlive = await SameAsWorkOrder1Async("olive", ct);

        var claim = Assert.Single(forPat.GetProperty("claims").EnumerateArray());
        Assert.Equal(("WO-3", 10), (claim.GetProperty("entity").GetProperty("key").GetString(), claim.GetProperty("value").GetInt32()));
        var hop = Assert.Single(claim.GetProperty("path").EnumerateArray());
        Assert.Equal(("same_item_order", "g-plant", "automatic"), (hop.GetProperty("relation").GetString(), hop.GetProperty("partition").GetString(), hop.GetProperty("identity").GetString()));
        Assert.False(hop.TryGetProperty("grounds", out _));

        Assert.Equal(("refused", "noVisibleResult"), (forQuinn.GetProperty("kind").GetString(), forQuinn.GetProperty("refusal").GetProperty("reason").GetString()));
        Assert.True(Assert.Single(forQuinn.GetProperty("partitions").EnumerateArray()).GetProperty("blocked").GetBoolean());
        Assert.DoesNotContain("WO-3", forQuinn.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("refused", forOlive.GetProperty("kind").GetString());
        Assert.False(forOlive.TryGetProperty("partitions", out _));
    }
}
