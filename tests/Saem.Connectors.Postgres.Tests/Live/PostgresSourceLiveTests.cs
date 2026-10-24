using Npgsql;
using Saem.Contracts;
using Saem.Ontology;
using Saem.Query;
using Xunit;

namespace Saem.Connectors.Postgres.Tests.Live;

/// <summary>
/// The adapter against the rig's mes source in its password-login profile: sessions opened as alice
/// and as bob, and the source's row-level security — not saem — deciding what each reads. alice sees
/// every work order; bob only those of line 1. WO-3's note is a canary only alice may see.
/// <para>
/// The logins are password logins that are members of each person's role, the interim the profile
/// documents: this shows a session reads as the person, not that the person's token reached the source.
/// </para>
/// </summary>
public class PostgresSourceLiveTests
{
    private const string Canary = "CANARY-7f3a-line2-only";

    private static readonly PostgresSource Mes = new(Environment.GetEnvironmentVariable("SAEM_RIG_MES")
        ?? throw new InvalidOperationException("SAEM_RIG_MES names the rig's mes source (tests/rig/compose.password-login.yaml)."));

    // Each person's password login is a member of their role, so their row rules are what it reads under.
    private static SourceOpener As(string person) => Mes.As($"{person}_pw", _ => ValueTask.FromResult($"rig-{person}-local-only"));

    [Fact]
    public async Task Each_person_reads_what_the_source_lets_them()
    {
        var ct = TestContext.Current.CancellationToken;

        await using (var alice = await As("alice")(ct))
        {
            var row = Assert.Single(await alice.ReadAsync("work_orders", "wo_no", "WO-3", ["note", "qty"], ct));
            Assert.Equal("WO-3", row.Key);
            Assert.Equal(Canary, row.Values["note"].GetString());
            Assert.Equal(10, row.Values["qty"].GetInt32());
        }

        await using var bob = await As("bob")(ct);
        Assert.Empty(await bob.ReadAsync("work_orders", "wo_no", "WO-3", ["note"], ct));
        var own = Assert.Single(await bob.ReadAsync("work_orders", "wo_no", "WO-1", ["item_code", "note"], ct));
        Assert.Equal("I-100", own.Values["item_code"].GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, own.Values["note"].ValueKind);
    }

    /// <summary>A find by a field reads the rows that hold the value under the person's own rules, in key order, up to the limit.</summary>
    [Fact]
    public async Task A_find_by_a_field_reads_only_the_rows_the_person_may_see()
    {
        var ct = TestContext.Current.CancellationToken;

        await using (var alice = await As("alice")(ct))
        {
            Assert.Equal(["WO-1", "WO-3"], (await alice.FindAsync("work_orders", "wo_no", "item_code", "I-100", ["qty"], 10, ct)).Select(r => r.Key));
            Assert.Equal(["WO-1"], (await alice.FindAsync("work_orders", "wo_no", "item_code", "I-100", [], 1, ct)).Select(r => r.Key));
        }

        await using var bob = await As("bob")(ct);
        Assert.Equal(["WO-1"], (await bob.FindAsync("work_orders", "wo_no", "item_code", "I-100", ["qty"], 10, ct)).Select(r => r.Key));
    }

    [Fact]
    public async Task A_scan_reads_every_row_the_person_may_see_in_key_order()
    {
        var ct = TestContext.Current.CancellationToken;

        await using (var alice = await As("alice")(ct))
        {
            var rows = await alice.ScanAsync("work_orders", "wo_no", ["note"], ct).ToListAsync(ct);
            Assert.Contains(rows, r => r.Key == "WO-3" && r.Values["note"].GetString() == Canary);
            Assert.Equal(rows.Select(r => r.Key).Order(StringComparer.Ordinal), rows.Select(r => r.Key));
        }

        await using var bob = await As("bob")(ct);
        var seen = await bob.ScanAsync("work_orders", "wo_no", ["note"], ct).ToListAsync(ct);
        Assert.Contains(seen, r => r.Key == "WO-1");
        Assert.DoesNotContain(seen, r => r.Key == "WO-3");
        Assert.DoesNotContain(Canary, string.Join(" ", seen.Select(r => r.Values["note"].ToString())), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_is_read_only_at_the_source()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var session = await As("alice")(ct);

        var setting = Assert.Single(await session.ReadAsync("pg_settings", "name", "transaction_read_only", ["setting"], ct));
        Assert.Equal("on", setting.Values["setting"].GetString());
    }

    [Fact]
    public async Task A_write_by_the_persons_login_is_refused_by_the_source_even_outside_a_read_only_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var login = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SAEM_RIG_MES")) { Username = "alice_pw", Password = "rig-alice-local-only" };
        await using var connection = new NpgsqlConnection(login.ConnectionString);
        await connection.OpenAsync(ct);
        await using var write = new NpgsqlCommand("BEGIN READ WRITE; UPDATE work_orders SET qty = qty WHERE wo_no = 'WO-1'", connection);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => write.ExecuteNonQueryAsync(ct));

        // The login holds SELECT only: the source refuses the write by its grants, not because saem never sends one.
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    [Fact]
    public async Task A_wrong_secret_is_refused_by_the_source()
    {
        var ct = TestContext.Current.CancellationToken;

        var refused = await Assert.ThrowsAsync<PostgresException>(async () =>
            await Mes.As("alice_pw", _ => ValueTask.FromResult("not-alices"))(ct));

        Assert.Equal(PostgresErrorCodes.InvalidPassword, refused.SqlState);
    }

    [Fact]
    public async Task A_lookup_walks_the_source_as_the_person_it_is_for()
    {
        var ct = TestContext.Current.CancellationToken;
        var ontology = Ontology.Ontology.Create(
        [
            new EntityTypeDeclaration("WorkOrder", "mes", "work_orders", "wo_no", new Dictionary<string, string> { ["note"] = "note", ["quantity"] = "qty" }),
        ]);
        var walker = new LookupWalker(new Fixed(ontology));
        var call = new LookupCall(new EntityKey("WorkOrder", "WO-3"), [], ["note"]);

        var forAlice = await walker.RunAsync(call, new PersonSessions(new Dictionary<string, SourceOpener> { ["mes"] = As("alice") }), ct);
        var forBob = await walker.RunAsync(call, new PersonSessions(new Dictionary<string, SourceOpener> { ["mes"] = As("bob") }), ct);

        var claim = Assert.Single(Assert.IsType<Answered>(forAlice).Claims);
        Assert.Equal(Canary, claim.Value.GetString());
        var refusal = Assert.IsType<Refused>(forBob).Refusal;
        Assert.Equal(RefusalReason.NoVisibleResult, refusal.Reason);
        Assert.DoesNotContain(Canary, System.Text.Json.JsonSerializer.Serialize(forBob), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bypass", false, true)]
    [InlineData("super", true, false)]
    public async Task A_login_row_level_security_does_not_apply_to_is_refused(string login, bool superuser, bool bypassesRls)
    {
        var ct = TestContext.Current.CancellationToken;

        // bypass_pw is a member of bob's role: with BYPASSRLS it would read the canary bob may not see.
        var refused = await Assert.ThrowsAsync<InsecureSourceRoleException>(async () => await (await As(login)(ct)).DisposeAsync());

        Assert.Equal($"{login}_pw", refused.RoleName);
        Assert.Equal(superuser, refused.IsSuperuser);
        Assert.Equal(bypassesRls, refused.BypassesRowLevelSecurity);
    }

    private sealed class Fixed(Ontology.Ontology ontology) : IOntologyCatalog
    {
        public ValueTask<Ontology.Ontology> GetAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(ontology);
    }
}
