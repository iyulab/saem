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

    private sealed class Fixed(Ontology.Ontology ontology) : IOntologyCatalog
    {
        public ValueTask<Ontology.Ontology> GetAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(ontology);
    }
}
