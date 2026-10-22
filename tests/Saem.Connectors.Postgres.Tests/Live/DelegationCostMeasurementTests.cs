using Npgsql;
using Saem.Contracts;
using Saem.Ontology;
using Saem.Query;
using Saem.Rig.Measurement;
using Xunit;

namespace Saem.Connectors.Postgres.Tests.Live;

/// <summary>
/// What a session per person costs mes, measured against the lines declared before measuring: a lookup
/// that opens its own session may add at most 100 ms at p95 over the same lookup on a connection opened
/// beforehand; and fifty people looking up at once may hold at most half the source's default
/// connection limit (PostgreSQL's 100 → 50). Crossing either line opens the evaluation of a shared
/// account with a session switch instead.
/// <para>
/// Run on purpose (SAEM_RIG_MEASURE=1), against the rig on the same host: a pass says «passes on a
/// local host», where the network round trip is smaller than any real one. The logins are passwords
/// over TLS, not a token exchanged per lookup — the exchange's own cost is not in these numbers.
/// </para>
/// </summary>
public class DelegationCostMeasurementTests
{
    private const int ConnectionLimit = 50;

    private static readonly string ConnectionString = Environment.GetEnvironmentVariable("SAEM_RIG_MES") ?? "";

    private static readonly LookupWalker Walker = new(new Fixed(Ontology.Ontology.Create(
    [
        new EntityTypeDeclaration("WorkOrder", "mes", "work_orders", "wo_no", new Dictionary<string, string> { ["quantity"] = "qty" }),
    ])));

    private static readonly LookupCall Quantity = new(new EntityKey("WorkOrder", "WO-1"), [], ["quantity"]);

    [Fact]
    public async Task A_session_per_person_stays_within_the_declared_lines()
    {
        if (Environment.GetEnvironmentVariable("SAEM_RIG_MEASURE") != "1")
        {
            Assert.Skip("A measurement, run on purpose: SAEM_RIG_MEASURE=1 against the rig's password-login profile.");
        }

        var ct = TestContext.Current.CancellationToken;
        var mes = new PostgresSource(ConnectionString);

        // The source's own view of its sessions, read by a login that is not one of the people measured.
        await using var monitor = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ConnectionString) { Username = "bob_pw", Password = "rig-bob-local-only", Pooling = false }.ConnectionString);
        await monitor.OpenAsync(ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE usename ~ '^u[0-9]{2}$'", monitor);

        var measured = await DelegationCost.MeasureAsync(
            Walker,
            Quantity,
            "mes",
            mes.As("alice_pw", _ => ValueTask.FromResult("rig-alice-local-only")),
            i => mes.As($"u{i:00}", _ => ValueTask.FromResult("rig-synthetic-local-only")),
            async () => (long)(await count.ExecuteScalarAsync(CancellationToken.None))!,
            ct);

        TestContext.Current.TestOutputHelper?.WriteLine(measured.Judge("mes (PostgreSQL)", ConnectionLimit));
    }

    private sealed class Fixed(Ontology.Ontology ontology) : IOntologyCatalog
    {
        public ValueTask<Ontology.Ontology> GetAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(ontology);
    }
}
