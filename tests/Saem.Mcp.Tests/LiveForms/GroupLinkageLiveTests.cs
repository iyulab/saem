using MySqlConnector;
using Npgsql;
using Saem.Connectors.MySql;
using Saem.Connectors.Postgres;
using Saem.Contracts;
using Saem.Materialization;
using Saem.Ontology;
using Saem.Query;
using Xunit;

namespace Saem.Mcp.Tests.LiveForms;

/// <summary>
/// saem D-87 on the rig: an identity across two systems by a linkage rule — suppliers in the mes (PostgreSQL) and vendors
/// in the erp (MySQL) that share no key — is fitted once to what the account reads, the fit kept in a group's store, and
/// scored with it: the store holds the pairs both fields agree on as things, verify agrees, and an entity's same-as is the
/// other system's entity. The two tables are made for the test by each source's superuser, granted to one person's
/// account (a mechanism check — the rig has no group account on the erp), and dropped after it.
/// </summary>
public sealed class GroupLinkageLiveTests : IAsyncLifetime
{
    private static readonly EntityIris Iris = new(new Uri("https://rig.saem.test/ontology/"));

    private static readonly string[] Companies =
    [
        "Hanbit Precision", "Daesung Metal", "Woori Hydraulics", "Seoul Bearing", "Mirae Plastics", "Kumho Castings",
        "Samil Valve", "Jinyang Coating", "Hyundai Springs", "Taeyang Gears", "Dongbang Seals", "Pohang Forging",
    ];

    private readonly string _suppliers = $"lk_sup_{Guid.NewGuid():N}"[..18];
    private readonly string _vendors = $"lk_ven_{Guid.NewGuid():N}"[..18];
    private GroupStoreRig _store = null!;

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is set by the rig's password-login profile (tests/rig/compose.password-login.yaml).");

    private static NpgsqlConnection MesSuper() => new(new NpgsqlConnectionStringBuilder(Required("SAEM_RIG_MES")) { Username = "super_pw", Password = "rig-super-local-only", Pooling = false }.ConnectionString);

    private static MySqlConnection ErpRoot() => new(new MySqlConnectionStringBuilder(Required("SAEM_RIG_ERP")) { UserID = "root", Password = "rig-superuser-local-only", Pooling = false }.ConnectionString);

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _store = await GroupStoreRig.StartAsync("g-plant", ct);

        // Ten suppliers kept again as vendors: the same registration number, every third name misspelt; two of each side
        // the other never had, one of them sharing a supplier's name but not its number.
        var suppliers = string.Join(", ", Enumerable.Range(0, 12).Select(n => $"('S-{n}', '{Companies[n]}', '1{n:00}-81-{n:00000}')"));
        var vendors = string.Join(", ", Enumerable.Range(0, 10)
            .Select(n => $"('V-{n}', '{(n % 3 == 0 ? Companies[n].Replace('a', 'e') : Companies[n])}', '1{n:00}-81-{n:00000}')")
            .Append("('V-10', 'Hanbit Precision', '999-81-99999')")
            .Append("('V-11', 'Gwangju Tooling', '998-81-99998')"));

        await using (var mes = MesSuper())
        {
            await mes.OpenAsync(ct);
            await using var create = new NpgsqlCommand(
                $"CREATE TABLE {_suppliers} (sup_id text PRIMARY KEY, sup_name text, biz_no text); INSERT INTO {_suppliers} VALUES {suppliers}; GRANT SELECT ON {_suppliers} TO alice;", mes);
            await create.ExecuteNonQueryAsync(ct);
        }

        await using (var erp = ErpRoot())
        {
            await erp.OpenAsync(ct);
            foreach (var statement in new[]
            {
                $"CREATE TABLE {_vendors} (vendor_id varchar(10) PRIMARY KEY, vendor_name varchar(40), reg_no varchar(20))",
                $"INSERT INTO {_vendors} VALUES {vendors}",
                $"GRANT SELECT ON {_vendors} TO 'alice_pw'@'%'",
            })
            {
                await using var command = new MySqlCommand(statement, erp);
                await command.ExecuteNonQueryAsync(ct);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await using (var mes = MesSuper())
        {
            await mes.OpenAsync(CancellationToken.None);
            await using var drop = new NpgsqlCommand($"DROP TABLE IF EXISTS {_suppliers}", mes);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using (var erp = ErpRoot())
        {
            await erp.OpenAsync(CancellationToken.None);
            await using var drop = new MySqlCommand($"DROP TABLE IF EXISTS {_vendors}", erp);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await _store.DisposeAsync();
    }

    [Fact]
    public async Task A_linkage_identity_across_two_systems_is_fitted_kept_scored_and_crossed()
    {
        var ct = TestContext.Current.CancellationToken;
        var ontology = Ontology.Ontology.Create(
        [
            new EntityTypeDeclaration("Supplier", "mes", _suppliers, "sup_id", new Dictionary<string, string> { ["name"] = "sup_name", ["bizNo"] = "biz_no" }),
            new EntityTypeDeclaration("Vendor", "erp", _vendors, "vendor_id", new Dictionary<string, string> { ["name"] = "vendor_name", ["regNo"] = "reg_no" }),
            new LinkageDeclaration("supplier-names", [new LinkageComparison("name", "name", LinkageMethod.JaroWinkler, [0.92]), new LinkageComparison("bizNo", "regNo")]),
            new IdentityDeclaration("same_supplier", "Supplier", "Vendor", IdentityDecision.Automatic) { Linkage = "supplier-names" },
        ]);
        var sessions = new PersonSessions(new Dictionary<string, SourceOpener>
        {
            ["mes"] = new PostgresSource(Required("SAEM_RIG_MES")).As("alice_pw", _ => ValueTask.FromResult("rig-alice-local-only")),
            ["erp"] = new MySqlSource(Required("SAEM_RIG_ERP")).As("alice_pw", _ => ValueTask.FromResult("rig-alice-local-only")),
        });
        var linker = new IdentityLinker(ontology, Iris);
        var store = new FormbaseIdentityStore(_store.Formbase);

        var fit = await linker.FitAsync(sessions, "same_supplier", DateTimeOffset.UtcNow, ct);
        Assert.Equal(24, fit.Records);
        await store.KeepFitAsync(ontology, fit, ct);
        var kept = await store.FitsAsync(ct);

        var skipped = new List<LinkageSkip>();
        await store.ReplaceAsync(ontology, linker.ScoreAsync(sessions, kept, skipped.Add, ct), ct);

        // The eight pairs whose name and number both agree are things; the two renamed past the threshold, and the vendor
        // that only shares a name, are not.
        Assert.Empty(skipped);
        var things = await store.ReadAsync(ct);
        Assert.Equal(
            [.. Enumerable.Range(0, 9).Where(n => n != 6).Select(n => $"{Iris.For("Supplier", $"S-{n}")} {Iris.For("Vendor", $"V-{n}")}")],
            things.Select(t => string.Join(' ', t.Members)).Order(StringComparer.Ordinal));
        Assert.True((await store.CompareAsync(ontology, linker.ScoreAsync(sessions, kept, cancellationToken: ct), ct)).Agrees);

        // Crossing the identity from a supplier in the mes reaches its vendor in the erp.
        var same = await store.SameAsAsync("same_supplier", Iris.For("Supplier", "S-1"), ct);
        Assert.Equal([Iris.For("Vendor", "V-1")], same.Members);
    }
}
