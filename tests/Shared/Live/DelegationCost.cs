using System.Diagnostics;
using Saem.Contracts;
using Saem.Query;
using Xunit;

namespace Saem.Rig.Measurement;

/// <summary>
/// What a session per person costs a source, measured the way the lines were declared before measuring:
/// a lookup that opens its own session — no pool, one connection to the source per lookup — against the
/// same lookup through one session opened beforehand and kept; then fifty people looking up at once,
/// each through a session of their own, counted on the client and sampled at the source.
/// <para>
/// Shared by each source adapter's measurement, so every source is measured by the same procedure and
/// only the source — its login, its connection count, its default connection limit — differs.
/// </para>
/// </summary>
internal static class DelegationCost
{
    public const int Lookups = 300;
    public const int Warmup = 20;
    public const int People = 50;

    /// <summary>The extra p95 a session per lookup may add over the kept session — the line a local measurement is judged by.</summary>
    public const double ExtraP95LimitMs = 100;

    /// <summary>
    /// Measures <paramref name="call"/> for one person (<paramref name="person"/>) and then for
    /// <see cref="People"/> synthetic people at once (<paramref name="synthetic"/>, numbered from 1),
    /// sampling <paramref name="countAtSource"/> while they run.
    /// </summary>
    public static async Task<Measured> MeasureAsync(
        LookupWalker walker,
        LookupCall call,
        string source,
        SourceOpener person,
        Func<int, SourceOpener> synthetic,
        Func<Task<long>> countAtSource,
        CancellationToken ct)
    {
        // ① Latency — the baseline reads through one session opened beforehand and kept.
        await using var shared = await person(ct);
        var baseline = await TimeAsync(walker, call, new PersonSessions(new Dictionary<string, SourceOpener> { [source] = _ => ValueTask.FromResult<ISourceSession>(new Kept(shared)) }), ct);
        var perLookup = await TimeAsync(walker, call, new PersonSessions(new Dictionary<string, SourceOpener> { [source] = person }), ct);

        // ② Connections — fifty people at once, each through a session of their own.
        var open = 0;
        var mostOpen = 0;
        var sampledMost = 0L;
        using var stop = new CancellationTokenSource();
        var sampling = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var n = await countAtSource();
                sampledMost = Math.Max(sampledMost, n);
            }
        }, CancellationToken.None);

        using var start = new Barrier(People);
        var answers = await Task.WhenAll(Enumerable.Range(1, People).Select(i => Task.Run(async () =>
        {
            var login = synthetic(i);
            SourceOpener counted = async c =>
            {
                var session = await login(c);
                var now = Interlocked.Increment(ref open);
                InterlockedMax(ref mostOpen, now);
                return new Counted(session, () => Interlocked.Decrement(ref open));
            };
            start.SignalAndWait(ct);
            return await walker.RunAsync(call, new PersonSessions(new Dictionary<string, SourceOpener> { [source] = counted }), ct);
        }, ct)));
        await stop.CancelAsync();
        await sampling;

        return new Measured(P95(baseline), P95(perLookup), mostOpen, sampledMost, answers);
    }

    private static async Task<List<double>> TimeAsync(LookupWalker walker, LookupCall call, PersonSessions sessions, CancellationToken ct)
    {
        var times = new List<double>(Lookups);
        for (var i = 0; i < Warmup + Lookups; i++)
        {
            var clock = Stopwatch.StartNew();
            Assert.IsType<Answered>(await walker.RunAsync(call, sessions, ct));
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

        public ValueTask<IReadOnlyList<SourceRow>> FindAsync(string table, string keyColumn, string column, string value, IReadOnlyList<string> columns, int limit, CancellationToken cancellationToken = default) =>
            inner.FindAsync(table, keyColumn, column, value, columns, limit, cancellationToken);

        public IAsyncEnumerable<SourceRow> ScanAsync(string table, string keyColumn, IReadOnlyList<string> columns, CancellationToken cancellationToken = default) =>
            inner.ScanAsync(table, keyColumn, columns, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Counted(ISourceSession inner, Action closed) : ISourceSession
    {
        public ValueTask<IReadOnlyList<SourceRow>> ReadAsync(string table, string keyColumn, string key, IReadOnlyList<string> columns, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(table, keyColumn, key, columns, cancellationToken);

        public ValueTask<IReadOnlyList<SourceRow>> FindAsync(string table, string keyColumn, string column, string value, IReadOnlyList<string> columns, int limit, CancellationToken cancellationToken = default) =>
            inner.FindAsync(table, keyColumn, column, value, columns, limit, cancellationToken);

        public IAsyncEnumerable<SourceRow> ScanAsync(string table, string keyColumn, IReadOnlyList<string> columns, CancellationToken cancellationToken = default) =>
            inner.ScanAsync(table, keyColumn, columns, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            closed();
        }
    }
}

/// <summary>One measurement: the two p95s, and the most sessions open at once on each side of the wire.</summary>
internal sealed record Measured(double BaselineP95, double PerLookupP95, int MostOpen, long MostSeenBySource, IReadOnlyList<LookupAnswer> Answers)
{
    public double Extra => PerLookupP95 - BaselineP95;

    public double Ratio => PerLookupP95 / BaselineP95;

    /// <summary>
    /// Judges the measurement against the declared lines and says what it measured. A local measurement
    /// is judged by the extra p95 alone: on one host the kept session's p95 is a fraction of a
    /// millisecond, so a ratio to it says nothing about whether a session per person breaks an
    /// interactive budget — the ratio is reported, and judged only where a real network round trip is
    /// in the numbers.
    /// </summary>
    public string Judge(string source, int connectionLimit)
    {
        var report =
            $"Q9 {source} (local rig, TLS, password logins, n={DelegationCost.Lookups}): baseline p95 {BaselineP95:F1} ms · session per lookup p95 {PerLookupP95:F1} ms · " +
            $"extra {Extra:F1} ms (line {DelegationCost.ExtraP95LimitMs} ms) · ratio {Ratio:F2} (reported, not judged locally) · " +
            $"{DelegationCost.People} people at once: most sessions open {MostOpen}, most connections seen by the source {MostSeenBySource} (line {connectionLimit})";

        Assert.All(Answers, a => Assert.IsType<Answered>(a));
        Assert.True(Extra <= DelegationCost.ExtraP95LimitMs, $"extra p95 {Extra:F1} ms crosses {DelegationCost.ExtraP95LimitMs} ms — {report}");
        Assert.True(MostOpen <= connectionLimit && MostSeenBySource <= connectionLimit, $"{Math.Max(MostOpen, MostSeenBySource)} connections at once cross {connectionLimit} — {report}");
        return report;
    }
}
