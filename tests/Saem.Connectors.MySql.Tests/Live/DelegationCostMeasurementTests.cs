using MySqlConnector;
using Saem.Contracts;
using Saem.Ontology;
using Saem.Query;
using Saem.Rig.Measurement;
using Xunit;

namespace Saem.Connectors.MySql.Tests.Live;

/// <summary>
/// What a session per person costs erp, measured against the same lines as mes: a lookup that opens its
/// own session may add at most 100 ms at p95 over the same lookup on a connection opened beforehand;
/// and fifty people looking up at once may hold at most half the source's default connection limit
/// (MySQL's 151 → 75). Crossing either line opens the evaluation of a shared account with a session
/// switch instead.
/// <para>
/// Run on purpose (SAEM_RIG_MEASURE=1), against the rig on the same host: a pass says «passes on a
/// local host». The logins are passwords over TLS, not a token exchanged per lookup.
/// </para>
/// </summary>
public class DelegationCostMeasurementTests
{
    private const int ConnectionLimit = 75;

    private static readonly string ConnectionString = Environment.GetEnvironmentVariable("SAEM_RIG_ERP") ?? "";

    private static readonly LookupWalker Walker = new(new Fixed(Ontology.Ontology.Create(
    [
        new EntityTypeDeclaration("Item", "erp", "visible_items", "item_code", new Dictionary<string, string> { ["name"] = "item_name" }),
    ])));

    private static readonly LookupCall Name = new(new EntityKey("Item", "I-100"), [], ["name"]);

    [Fact]
    public async Task A_session_per_person_stays_within_the_declared_lines()
    {
        if (Environment.GetEnvironmentVariable("SAEM_RIG_MEASURE") != "1")
        {
            Assert.Skip("A measurement, run on purpose: SAEM_RIG_MEASURE=1 against the rig's password-login profile.");
        }

        var ct = TestContext.Current.CancellationToken;
        var erp = new MySqlSource(ConnectionString);

        // The source's own view of its sessions: MySQL shows another account's threads only to a login
        // with PROCESS, so the rig's monitor login (initdb/30-password-login.sh) reads them.
        await using var monitor = new MySqlConnection(new MySqlConnectionStringBuilder(ConnectionString) { UserID = "rig_monitor", Password = "rig-monitor-local-only", Database = "performance_schema", Pooling = false }.ConnectionString);
        await monitor.OpenAsync(ct);
        await using var count = new MySqlCommand("SELECT count(*) FROM performance_schema.threads WHERE PROCESSLIST_USER REGEXP '^u[0-9]{2}$'", monitor);

        var measured = await DelegationCost.MeasureAsync(
            Walker,
            Name,
            "erp",
            erp.As("alice_pw", _ => ValueTask.FromResult("rig-alice-local-only")),
            i => erp.As($"u{i:00}", _ => ValueTask.FromResult("rig-synthetic-local-only")),
            async () => Convert.ToInt64(await count.ExecuteScalarAsync(CancellationToken.None), System.Globalization.CultureInfo.InvariantCulture),
            ct);

        TestContext.Current.TestOutputHelper?.WriteLine(measured.Judge("erp (MySQL)", ConnectionLimit));
    }

    private sealed class Fixed(Ontology.Ontology ontology) : IOntologyCatalog
    {
        public ValueTask<Ontology.Ontology> GetAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(ontology);
    }
}
