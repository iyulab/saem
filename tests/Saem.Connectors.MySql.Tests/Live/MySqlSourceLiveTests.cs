using MySqlConnector;
using Saem.Connectors.Postgres;
using Saem.Contracts;
using Saem.Ontology;
using Saem.Query;
using Xunit;

namespace Saem.Connectors.MySql.Tests.Live;

/// <summary>
/// The adapter against the rig's erp source in its password-login profile, and the lookup that
/// crosses both of the rig's systems: a work order in mes (PostgreSQL, row-level security) names an
/// item in erp (MySQL, views over an access table), which names its supplier. Supplier S-2's name is a
/// canary only alice may see; bob may not see work order WO-3 at all.
/// <para>
/// The logins are password logins each source treats as the person, the interim the profile documents:
/// this shows each session reads as the person, not that the person's token reached the source.
/// </para>
/// </summary>
public class MySqlSourceLiveTests
{
    private const string Canary = "CANARY-91c4-supplier-two";

    private static readonly MySqlSource Erp = new(Required("SAEM_RIG_ERP"));
    private static readonly PostgresSource Mes = new(Required("SAEM_RIG_MES"));

    private static string Required(string variable) => Environment.GetEnvironmentVariable(variable)
        ?? throw new InvalidOperationException($"{variable} names one of the rig's sources (tests/rig/compose.password-login.yaml).");

    private static string Secret(string person) => $"rig-{person}-local-only";

    private static PersonSessions SessionsOf(string person) => new(new Dictionary<string, SourceOpener>
    {
        ["mes"] = Mes.As($"{person}_pw", _ => ValueTask.FromResult(Secret(person))),
        ["erp"] = Erp.As($"{person}_pw", _ => ValueTask.FromResult(Secret(person))),
    });

    private static readonly LookupWalker Walker = new(new Fixed(Ontology.Ontology.Create(
    [
        new EntityTypeDeclaration("WorkOrder", "mes", "work_orders", "wo_no", new Dictionary<string, string> { ["item"] = "item_code", ["quantity"] = "qty" }),
        new EntityTypeDeclaration("Item", "erp", "visible_items", "item_code", new Dictionary<string, string> { ["name"] = "item_name", ["supplier"] = "supplier_id" }),
        new EntityTypeDeclaration("Supplier", "erp", "visible_suppliers", "supplier_id", new Dictionary<string, string> { ["name"] = "supplier_name" }),
        new RelationDeclaration("orders_item", "WorkOrder", "item", "Item"),
        new RelationDeclaration("supplied_by", "Item", "supplier", "Supplier"),
    ])));

    [Fact]
    public async Task Each_person_reads_what_the_source_lets_them()
    {
        var ct = TestContext.Current.CancellationToken;

        await using (var alice = await Erp.As("alice_pw", _ => ValueTask.FromResult(Secret("alice")))(ct))
        {
            var row = Assert.Single(await alice.ReadAsync("visible_suppliers", "supplier_id", "S-2", ["supplier_name"], ct));
            Assert.Equal(Canary, row.Values["supplier_name"].GetString());
        }

        await using var bob = await Erp.As("bob_pw", _ => ValueTask.FromResult(Secret("bob")))(ct);
        Assert.Empty(await bob.ReadAsync("visible_suppliers", "supplier_id", "S-2", ["supplier_name"], ct));
        Assert.Equal("Hanbit Parts", Assert.Single(await bob.ReadAsync("visible_suppliers", "supplier_id", "S-1", ["supplier_name"], ct)).Values["supplier_name"].GetString());
    }

    /// <summary>A find by a field reads the rows that hold the value under the person's own rules.</summary>
    [Fact]
    public async Task A_find_by_a_field_reads_only_the_rows_the_person_may_see()
    {
        var ct = TestContext.Current.CancellationToken;

        await using (var alice = await Erp.As("alice_pw", _ => ValueTask.FromResult(Secret("alice")))(ct))
        {
            Assert.Equal(["S-2"], (await alice.FindAsync("visible_suppliers", "supplier_id", "supplier_name", Canary, ["supplier_name"], 10, ct)).Select(r => r.Key));
        }

        await using var bob = await Erp.As("bob_pw", _ => ValueTask.FromResult(Secret("bob")))(ct);
        Assert.Empty(await bob.FindAsync("visible_suppliers", "supplier_id", "supplier_name", Canary, ["supplier_name"], 10, ct));
        Assert.Equal(["S-1"], (await bob.FindAsync("visible_suppliers", "supplier_id", "supplier_name", "Hanbit Parts", [], 10, ct)).Select(r => r.Key));
    }

    [Fact]
    public async Task A_scan_reads_every_row_the_person_may_see_in_key_order()
    {
        var ct = TestContext.Current.CancellationToken;

        await using (var alice = await Erp.As("alice_pw", _ => ValueTask.FromResult(Secret("alice")))(ct))
        {
            var rows = await alice.ScanAsync("visible_suppliers", "supplier_id", ["supplier_name"], ct).ToListAsync(ct);
            Assert.Contains(rows, r => r.Key == "S-2" && r.Values["supplier_name"].GetString() == Canary);
            Assert.Equal(rows.Select(r => r.Key).Order(StringComparer.Ordinal), rows.Select(r => r.Key));
        }

        await using var bob = await Erp.As("bob_pw", _ => ValueTask.FromResult(Secret("bob")))(ct);
        var seen = await bob.ScanAsync("visible_suppliers", "supplier_id", ["supplier_name"], ct).ToListAsync(ct);
        Assert.Contains(seen, r => r.Key == "S-1");
        Assert.DoesNotContain(seen, r => r.Key == "S-2");
    }

    [Fact]
    public async Task A_session_carries_its_name_where_the_source_records_it()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var session = await Erp.As("alice_pw", _ => ValueTask.FromResult(Secret("alice")), "saem/lookup-under-test")(ct);

        // The source keeps a session's connection attributes, program_name among them, for its account's sessions.
        var row = Assert.Single(await session.ReadAsync("performance_schema.session_account_connect_attrs", "ATTR_NAME", "program_name", ["ATTR_VALUE"], ct));
        Assert.Equal("saem/lookup-under-test", row.Values["ATTR_VALUE"].GetString());
    }

    [Fact]
    public async Task A_write_by_the_persons_login_is_refused_by_the_source()
    {
        var ct = TestContext.Current.CancellationToken;
        var login = new MySqlConnectionStringBuilder(Required("SAEM_RIG_ERP")) { UserID = "alice_pw", Password = Secret("alice") };
        await using var connection = new MySqlConnection(login.ConnectionString);
        await connection.OpenAsync(ct);
        await using var write = new MySqlCommand("UPDATE visible_items SET item_name = item_name", connection);

        var refused = await Assert.ThrowsAsync<MySqlException>(() => write.ExecuteNonQueryAsync(ct));

        // The login may SELECT the views and nothing else: the source refuses the write by its grants.
        Assert.Equal(MySqlErrorCode.TableAccessDenied, refused.ErrorCode);
    }

    [Fact]
    public async Task A_wrong_secret_is_refused_by_the_source()
    {
        var ct = TestContext.Current.CancellationToken;

        var refused = await Assert.ThrowsAsync<MySqlException>(async () =>
            await Erp.As("alice_pw", _ => ValueTask.FromResult("not-alices"))(ct));

        Assert.Equal(MySqlErrorCode.AccessDenied, refused.ErrorCode);
    }

    [Fact]
    public async Task A_lookup_across_both_systems_answers_each_person_from_what_they_may_see()
    {
        var ct = TestContext.Current.CancellationToken;
        var supplierOf = new LookupCall(new EntityKey("WorkOrder", "WO-2"), ["orders_item", "supplied_by"], ["name"]);

        var forAlice = Assert.IsType<Answered>(await Walker.RunAsync(supplierOf, SessionsOf("alice"), ct));
        var claim = Assert.Single(forAlice.Claims);
        Assert.Equal(Canary, claim.Value.GetString());
        // The path names both systems' entities: a work order in mes, the item and supplier in erp.
        Assert.Equal(["orders_item", "supplied_by"], claim.Path.Select(h => h.Relation));
        Assert.Equal(new EntityKey("WorkOrder", "WO-2"), claim.Path[0].From);
        Assert.Equal(new EntityKey("Item", "I-200"), claim.Path[0].To);
        Assert.Equal(new EntityKey("Supplier", "S-2"), claim.Path[1].To);

        // bob may see WO-2 in mes, but erp shows him only the items of suppliers he may see — not I-200,
        // which S-2 supplies: the walk is refused at the hop into erp.
        var forBob = Assert.IsType<Refused>(await Walker.RunAsync(supplierOf, SessionsOf("bob"), ct));
        Assert.Equal(1, forBob.Refusal.AtHop);
        Assert.Equal("orders_item", forBob.Refusal.Relation);
        Assert.Equal("erp", forBob.Refusal.System);
        Assert.DoesNotContain(Canary, System.Text.Json.JsonSerializer.Serialize<LookupAnswer>(forBob), StringComparison.Ordinal);

        // A work order bob may not see is refused at the start, in the other system.
        var hidden = new LookupCall(new EntityKey("WorkOrder", "WO-3"), ["orders_item", "supplied_by"], ["name"]);
        var refusedAtStart = Assert.IsType<Refused>(await Walker.RunAsync(hidden, SessionsOf("bob"), ct));
        Assert.Equal(0, refusedAtStart.Refusal.AtHop);
        Assert.Equal("mes", refusedAtStart.Refusal.System);
        Assert.Equal("Hanbit Parts", Assert.Single(Assert.IsType<Answered>(await Walker.RunAsync(hidden, SessionsOf("alice"), ct)).Claims).Value.GetString());
    }

    private sealed class Fixed(Ontology.Ontology ontology) : IOntologyCatalog
    {
        public ValueTask<Ontology.Ontology> GetAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(ontology);
    }
}
