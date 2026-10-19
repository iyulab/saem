using System.Diagnostics;
using Npgsql;
using Saem.Contracts;
using Saem.Ontology;
using Saem.Query;
using Xunit;

namespace Saem.Connectors.Postgres.Tests.Live;

/// <summary>
/// What a session per person costs, measured against the lines declared before measuring: a lookup
/// that opens its own session — no pool, one connection to the source per lookup — may add at most
/// 100 ms at p95 over the same lookup on a connection opened beforehand, and take at most twice that
/// baseline's p95; and fifty people looking up at once may hold at most half the source's default
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
    private const int Lookups = 300;
    private const int Warmup = 20;
    private const int People = 50;
    private const double ExtraP95LimitMs = 100;
    private const double BaselineRatioLimit = 2;
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
        var alice = mes.As("alice_pw", _ => ValueTask.FromResult("rig-alice-local-only"));

        // ① Latency — the baseline reads through one session opened beforehand and kept.
        await using var shared = await alice(ct);
        var reused = new PersonSessions(new Dictionary<string, SourceOpener> { ["mes"] = _ => ValueTask.FromResult<ISourceSession>(new Kept(shared)) });
        var baseline = await TimeAsync(reused, ct);
        var perPerson = await TimeAsync(new PersonSessions(new Dictionary<string, SourceOpener> { ["mes"] = alice }), ct);

        var baseP95 = P95(baseline);
        var ownP95 = P95(perPerson);

        // ② Connections — fifty people at once, each through a session of their own.
        var open = 0;
        var mostOpen = 0;
        var sampledMost = 0L;
        using var stop = new CancellationTokenSource();
        await using var monitor = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ConnectionString) { Username = "bob_pw", Password = "rig-bob-local-only", Pooling = false }.ConnectionString);
        await monitor.OpenAsync(ct);
        var sampling = Task.Run(async () =>
        {
            await using var count = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE usename ~ '^u[0-9]{2}$'", monitor);
            while (!stop.IsCancellationRequested)
            {
                var n = (long)(await count.ExecuteScalarAsync(CancellationToken.None))!;
                sampledMost = Math.Max(sampledMost, n);
            }
        }, CancellationToken.None);

        using var start = new Barrier(People);
        var answers = await Task.WhenAll(Enumerable.Range(1, People).Select(i => Task.Run(async () =>
        {
            var login = mes.As($"u{i:00}", _ => ValueTask.FromResult("rig-synthetic-local-only"));
            SourceOpener counted = async c =>
            {
                var session = await login(c);
                var now = Interlocked.Increment(ref open);
                InterlockedMax(ref mostOpen, now);
                return new Counted(session, () => Interlocked.Decrement(ref open));
            };
            start.SignalAndWait(ct);
            return await Walker.RunAsync(Quantity, new PersonSessions(new Dictionary<string, SourceOpener> { ["mes"] = counted }), ct);
        }, ct)));
        await stop.CancelAsync();
        await sampling;

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Q9 (local rig, TLS, password logins, n={Lookups}): baseline p95 {baseP95:F1} ms · session per lookup p95 {ownP95:F1} ms · " +
            $"extra {ownP95 - baseP95:F1} ms (line {ExtraP95LimitMs} ms) · ratio {ownP95 / baseP95:F2} (line {BaselineRatioLimit}) · " +
            $"{People} people at once: most sessions open {mostOpen}, most connections seen by the source {sampledMost} (line {ConnectionLimit})");

        Assert.All(answers, a => Assert.IsType<Answered>(a));
        Assert.True(ownP95 - baseP95 <= ExtraP95LimitMs, $"extra p95 {ownP95 - baseP95:F1} ms crosses {ExtraP95LimitMs} ms");
        Assert.True(ownP95 <= BaselineRatioLimit * baseP95, $"p95 {ownP95:F1} ms is {ownP95 / baseP95:F2}× the baseline's {baseP95:F1} ms — the line is {BaselineRatioLimit}×");
        Assert.True(mostOpen <= ConnectionLimit && sampledMost <= ConnectionLimit, $"{Math.Max(mostOpen, sampledMost)} connections at once cross {ConnectionLimit}");
    }

    private static async Task<List<double>> TimeAsync(PersonSessions sessions, CancellationToken ct)
    {
        var times = new List<double>(Lookups);
        for (var i = 0; i < Warmup + Lookups; i++)
        {
            var clock = Stopwatch.StartNew();
            Assert.IsType<Answered>(await Walker.RunAsync(Quantity, sessions, ct));
            if (i >= Warmup)
            {
                times.Add(clock.Elapsed.TotalMilliseconds);
            }
        }

        return times;
    }

    private static double P95(List<double> times)
    {
        var sorted = times.Order().ToList();
        return sorted[(int)Math.Ceiling(0.95 * sorted.Count) - 1];
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    /// <summary>The baseline's session: kept open across lookups, so a lookup's end does not close it.</summary>
    private sealed class Kept(ISourceSession inner) : ISourceSession
    {
        public ValueTask<IReadOnlyList<SourceRow>> ReadAsync(string table, string keyColumn, string key, IReadOnlyList<string> columns, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(table, keyColumn, key, columns, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Counted(ISourceSession inner, Action closed) : ISourceSession
    {
        public ValueTask<IReadOnlyList<SourceRow>> ReadAsync(string table, string keyColumn, string key, IReadOnlyList<string> columns, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(table, keyColumn, key, columns, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            closed();
        }
    }

    private sealed class Fixed(Ontology.Ontology ontology) : IOntologyCatalog
    {
        public ValueTask<Ontology.Ontology> GetAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(ontology);
    }
}
