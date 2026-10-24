using System.Text.Json;
using Saem.Contracts;
using Saem.Ontology;

namespace Saem.Query;

/// <summary>
/// Runs a lookup: plans it in the ontology's terms, then reads the start entity, follows each
/// relation by the value of its field to the next entity — possibly in another system — and reads
/// the asked fields of the entity the walk ends at. Every read goes through a session opened as the
/// person the lookup is for. The answer is built from what the reads returned and nothing else;
/// where a read returns nothing, the lookup is refused at that hop.
/// <para>
/// A step that walks a relation backwards — anywhere in the path — asks the person's materialized partitions which entities
/// lead to the one reached, then reads each of those as the person — keeping only the ones the source
/// shows them and still says lead there. The edges find the candidates; the answer is still only what
/// the person's own reads returned. Each partition read is reported on the answer: whether it led to entities
/// the person's reads did not show (blocked), and whether it was built in full.
/// </para>
/// <para>
/// A step whose candidates exceed <see cref="MaxCandidatesPerStep"/> is not read at all (<see cref="TooBroadException"/>):
/// each candidate is a read as the person, so the limit bounds what one step costs.
/// </para>
/// </summary>
public sealed class LookupWalker
{
    /// <summary>The default limit on one step's candidates (saem D-84).</summary>
    public const int DefaultMaxCandidatesPerStep = 50;

    private readonly IOntologyCatalog _catalog;
    private readonly TimeProvider _time;
    private readonly IProjectionStates _projections;

    /// <summary>Creates a walker over the ontology <paramref name="catalog"/> holds.</summary>
    /// <param name="catalog">The ontology.</param>
    /// <param name="time">The clock a source record's read time is taken from; omitted, the system's.</param>
    /// <param name="maxCandidatesPerStep">The most candidates one step may read; omitted, <see cref="DefaultMaxCandidatesPerStep"/>.</param>
    /// <param name="projections">
    /// Which tables are projections of documents kept elsewhere and how far behind each is — said on the
    /// sources read from them, and on a refusal at them; omitted, no table is.
    /// </param>
    public LookupWalker(IOntologyCatalog catalog, TimeProvider? time = null, int maxCandidatesPerStep = DefaultMaxCandidatesPerStep, IProjectionStates? projections = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCandidatesPerStep, 1);
        _catalog = catalog;
        _time = time ?? TimeProvider.System;
        _projections = projections ?? NoProjectionStates.Instance;
        MaxCandidatesPerStep = maxCandidatesPerStep;
    }

    /// <summary>The most candidates one step may read before it is refused as too broad.</summary>
    public int MaxCandidatesPerStep { get; }

    /// <summary>Runs <paramref name="call"/> for the person <paramref name="sessions"/> reads as, who reads no materialized partition.</summary>
    /// <exception cref="UnknownTermException">The lookup is not in the ontology's terms; no source was read.</exception>
    /// <exception cref="InvalidOperationException">A source returned more than one row for a declared key.</exception>
    public Task<LookupAnswer> RunAsync(LookupCall call, ISourceSessions sessions, CancellationToken cancellationToken = default) =>
        RunAsync(call, sessions, NoMaterializedRelations.Instance, cancellationToken);

    /// <summary>
    /// Runs <paramref name="call"/> for the person <paramref name="sessions"/> reads as, walking a relation
    /// backwards through <paramref name="relations"/> — the partitions of that person's groups.
    /// </summary>
    /// <exception cref="UnknownTermException">The lookup is not in the ontology's terms; no source was read.</exception>
    /// <exception cref="TooBroadException">A step has more candidates than <see cref="MaxCandidatesPerStep"/>; none of them was read.</exception>
    /// <exception cref="InvalidOperationException">A source returned more than one row for a declared key.</exception>
    public async Task<LookupAnswer> RunAsync(LookupCall call, ISourceSessions sessions, IMaterializedRelations relations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(relations);

        var ontology = await _catalog.GetAsync(cancellationToken).ConfigureAwait(false);
        var plan = ontology.Plan(call);
        var answer = await WalkAsync(call, plan, ontology, sessions, relations, cancellationToken).ConfigureAwait(false);
        return answer with { CatalogVersion = ontology.Version };
    }

    private async Task<LookupAnswer> WalkAsync(
        LookupCall call, LookupPlan plan, Ontology.Ontology ontology, ISourceSessions sessions, IMaterializedRelations relations, CancellationToken cancellationToken)
    {

        var open = new Dictionary<string, ISourceSession>(StringComparer.Ordinal);
        var states = new Dictionary<(string, string), SourceProjection?>();
        try
        {
            var at = plan.Start;
            string key;
            if (call.Start.Where is { } where)
            {
                // Named by a field's value: one visible match is walked from; several are offered to choose from.
                var found = await FindAsync(open, sessions, at, where, MaxCandidatesPerStep + 1, cancellationToken).ConfigureAwait(false);
                if (found.Count == 0)
                {
                    return Refuse(0, null, at.System, await StateAsync(states, at, cancellationToken).ConfigureAwait(false));
                }

                if (found.Count > MaxCandidatesPerStep)
                {
                    throw TooBroadException.AtStart(at.Name, where.Field, MaxCandidatesPerStep);
                }

                if (found.Count > 1)
                {
                    var readAt = _time.GetUtcNow();
                    var projection = await StateAsync(states, at, cancellationToken).ConfigureAwait(false);
                    return new Candidates([.. found.Select(row => new Candidate(
                        new EntityKey(at.Name, row.Key), new SourceRecord(at.System, at.Table, RecordOf(at, row)) { ReadAt = readAt, Projection = projection }))]);
                }

                key = at.Normalize(found[0].Key);
            }
            else
            {
                key = at.Normalize(call.Start.Key!);
            }

            return await StepsAsync(open, states, sessions, relations, plan, ontology, at, key, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var session in open.Values)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>One way a walk has come so far: the entity it stands on, the hops that reached it, and its row when already read.</summary>
    private sealed record Branch(string Key, IReadOnlyList<Hop> Hops, SourceRow? Row);

    /// <summary>
    /// Walks the plan's steps from <paramref name="key"/>. At every step all branches stand on the same type, so a
    /// step is taken for all of them at once: forwards, each branch reads its row and follows the field to one next
    /// entity; backwards, each branch asks the partitions which entities lead to it and reads each of those as the
    /// person — a branch becomes as many as survive. Where no branch survives, the walk is refused at that hop.
    /// </summary>
    private async Task<LookupAnswer> StepsAsync(
        Dictionary<string, ISourceSession> open,
        Dictionary<(string, string), SourceProjection?> states,
        ISourceSessions sessions,
        IMaterializedRelations relations,
        LookupPlan plan,
        Ontology.Ontology ontology,
        EntityTypeDeclaration at,
        string key,
        CancellationToken cancellationToken)
    {
        var reports = new Dictionary<string, PartitionRead>(StringComparer.Ordinal);
        LookupAnswer With(LookupAnswer answer) =>
            reports.Count > 0 ? answer with { Partitions = [.. reports.Values.OrderBy(r => r.Name, StringComparer.Ordinal)] } : answer;

        IReadOnlyList<Branch> frontier = [new Branch(key, [], null)];
        for (var hop = 0; ; hop++)
        {
            var step = hop < plan.Steps.Count ? plan.Steps[hop] : null;
            if (step is { Backwards: true })
            {
                var reached = ontology.EntityTypes[step.Reaches];
                var walked = await BackwardsAsync(open, states, sessions, relations, step, at, reached, frontier, hop + 1, Needed(plan, hop + 1), reports, cancellationToken).ConfigureAwait(false);
                if (walked is null)
                {
                    return With(new Refused(new Refusal(RefusalReason.NoMaterializedPartition, hop + 1, step.Name, reached.System)));
                }

                if (walked.Count == 0)
                {
                    return With(Refuse(hop + 1, step.Name, reached.System, reports.Count > 0 ? await StateAsync(states, reached, cancellationToken).ConfigureAwait(false) : null));
                }

                frontier = walked;
                at = reached;
                continue;
            }

            // Forwards, or the end: each branch's row, with the fields this step needs.
            var fields = Needed(plan, hop);
            var survivors = new List<(Branch Branch, SourceRow Row)>();
            foreach (var branch in frontier)
            {
                if ((branch.Row ?? await ReadAsync(open, sessions, at, branch.Key, fields, cancellationToken).ConfigureAwait(false)) is { } row)
                {
                    survivors.Add((branch, row));
                }
            }

            if (survivors.Count == 0)
            {
                return With(Refuse(hop, hop == 0 ? null : plan.Steps[hop - 1].Name, at.System, await StateAsync(states, at, cancellationToken).ConfigureAwait(false)));
            }

            if (step is null)
            {
                var projection = await StateAsync(states, at, cancellationToken).ConfigureAwait(false);
                return With(new Answered([.. survivors.SelectMany(s => Answer(at, s.Row, _time.GetUtcNow(), projection, plan.Read, s.Branch.Hops).Claims)]));
            }

            var to = ontology.EntityTypes[step.Reaches];
            var next = new List<Branch>();
            foreach (var (branch, row) in survivors)
            {
                // An empty field names nothing to follow, which the answer cannot tell apart from a next entity the person may not see.
                if (Text(row.Values[at.Fields[step.Relation.Via]]) is { } via)
                {
                    // The field holds the next entity's key as the next type spells keys.
                    var nextKey = to.Normalize(via);
                    next.Add(new Branch(nextKey, [.. branch.Hops, new Hop(step.Name, new EntityKey(at.Name, row.Key), new EntityKey(to.Name, nextKey))], null));
                }
            }

            if (next.Count == 0)
            {
                return With(Refuse(hop + 1, step.Name, to.System));
            }

            frontier = next;
            at = to;
        }
    }

    /// <summary>The fields a branch's row must hold at <paramref name="hop"/>: the asked ones at the end, the relation's field before a forward step, none before a backward one.</summary>
    private static IReadOnlyList<string> Needed(LookupPlan plan, int hop) =>
        hop >= plan.Steps.Count ? plan.Read
        : plan.Steps[hop].Backwards ? []
        : [plan.Steps[hop].Relation.Via];

    /// <summary>
    /// A step walked backwards, for every branch: in each partition, the entities whose relation leads to the
    /// branch's entity, each read as the person with the relation's field and what the next step needs — kept when
    /// the source shows the row and the field still names that entity. An edge built before the source changed, or
    /// one to a row this person may not see, adds nothing; its partition is then reported blocked, which says that
    /// much and no more. Null when the person is in no partition.
    /// </summary>
    private async Task<IReadOnlyList<Branch>?> BackwardsAsync(
        Dictionary<string, ISourceSession> open,
        Dictionary<(string, string), SourceProjection?> states,
        ISourceSessions sessions,
        IMaterializedRelations relations,
        LookupStep step,
        EntityTypeDeclaration leaves,
        EntityTypeDeclaration from,
        IReadOnlyList<Branch> frontier,
        int atHop,
        IReadOnlyList<string> needed,
        Dictionary<string, PartitionRead> reports,
        CancellationToken cancellationToken)
    {
        if (relations.Partitions.Count == 0)
        {
            return null;
        }

        // Every candidate of the step is counted before any is read: a step too broad anywhere reads none.
        var leading = new List<(string Partition, Branch Branch, PartitionEdges Edges)>();
        var candidates = 0;
        foreach (var partition in relations.Partitions)
        {
            foreach (var branch in frontier)
            {
                var edges = await relations.LeadingToAsync(partition, step.Relation, branch.Key, MaxCandidatesPerStep - candidates, cancellationToken).ConfigureAwait(false);
                candidates += edges.Keys.Count;
                if (candidates > MaxCandidatesPerStep)
                {
                    throw new TooBroadException(atHop, step.Name, MaxCandidatesPerStep);
                }

                leading.Add((partition, branch, edges));
            }
        }

        var next = new List<Branch>();
        var columns = needed.Append(step.Relation.Via).Distinct(StringComparer.Ordinal).ToList();
        foreach (var (partition, branch, edges) in leading)
        {
            if (edges.Keys.Count == 0 && !edges.Incomplete)
            {
                // Led nowhere and holds everything it was given: nothing to say of it.
                continue;
            }

            // Asked only of a partition that has something to report, once.
            var report = reports.TryGetValue(partition, out var known)
                ? known
                : new PartitionRead(partition, await relations.AsOfAsync(partition, cancellationToken).ConfigureAwait(false), Blocked: false, Incomplete: false);
            var blocked = false;
            foreach (var key in edges.Keys)
            {
                var row = await ReadAsync(open, sessions, from, key, columns, cancellationToken).ConfigureAwait(false);
                // The field still names the branch's entity — as that entity's type spells keys.
                if (row is null || Text(row.Values[from.Fields[step.Relation.Via]]) is not { } leadsTo || leaves.Normalize(leadsTo) != branch.Key)
                {
                    blocked = true;
                    continue;
                }

                var back = new Hop(step.Name, new EntityKey(leaves.Name, branch.Key), new EntityKey(from.Name, row.Key)) { Partition = partition, PartitionAsOf = report.AsOf };
                next.Add(new Branch(from.Normalize(row.Key), [.. branch.Hops, back], row));
            }

            reports[partition] = report with { Blocked = report.Blocked || blocked, Incomplete = report.Incomplete || edges.Incomplete };
        }

        return next;
    }

    /// <summary>The rows of <paramref name="type"/> whose field holds the value, with the record columns — at most <paramref name="limit"/>.</summary>
    private static async Task<IReadOnlyList<SourceRow>> FindAsync(
        Dictionary<string, ISourceSession> open,
        ISourceSessions sessions,
        EntityTypeDeclaration type,
        FieldMatch where,
        int limit,
        CancellationToken cancellationToken)
    {
        var session = await SessionAsync(open, sessions, type.System, cancellationToken).ConfigureAwait(false);
        return await session.FindAsync(type.Table, type.Key, type.Fields[where.Field], where.Value, [.. type.Record ?? []], limit, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ISourceSession> SessionAsync(Dictionary<string, ISourceSession> open, ISourceSessions sessions, string system, CancellationToken cancellationToken)
    {
        if (!open.TryGetValue(system, out var session))
        {
            session = await sessions.OpenAsync(system, cancellationToken).ConfigureAwait(false);
            open.Add(system, session);
        }

        return session;
    }

    private static async Task<SourceRow?> ReadAsync(
        Dictionary<string, ISourceSession> open,
        ISourceSessions sessions,
        EntityTypeDeclaration type,
        string key,
        IReadOnlyList<string> fields,
        CancellationToken cancellationToken)
    {
        var session = await SessionAsync(open, sessions, type.System, cancellationToken).ConfigureAwait(false);

        // The record columns are read beside the asked ones: the answer cites the record a row is.
        var columns = fields.Select(f => type.Fields[f]).Concat(type.Record ?? []).Distinct(StringComparer.Ordinal).ToList();
        var rows = await session.ReadAsync(type.Table, type.Key, key, columns, cancellationToken).ConfigureAwait(false);
        return rows.Count switch
        {
            0 => null,
            1 => rows[0],
            _ => throw new InvalidOperationException(
                $"'{type.System}' returned {rows.Count} rows of '{type.Table}' for key '{key}' in '{type.Key}', which the ontology declares as the key of '{type.Name}'."),
        };
    }

    /// <param name="readAt">When the row came back from its source — taken as each read returns, so a walk that
    /// crosses systems says when each one was read.</param>
    /// <summary>How far <paramref name="type"/>'s table is behind its documents — asked once per table per lookup.</summary>
    private async ValueTask<SourceProjection?> StateAsync(Dictionary<(string, string), SourceProjection?> states, EntityTypeDeclaration type, CancellationToken cancellationToken)
    {
        if (!states.TryGetValue((type.System, type.Table), out var state))
        {
            state = await _projections.StateOfAsync(type.System, type.Table, cancellationToken).ConfigureAwait(false);
            states.Add((type.System, type.Table), state);
        }

        return state;
    }

    private static Answered Answer(EntityTypeDeclaration type, SourceRow row, DateTimeOffset readAt, SourceProjection? projection, IReadOnlyList<string> read, IReadOnlyList<Hop> hops)
    {
        var entity = new EntityKey(type.Name, row.Key);
        var source = new SourceRecord(type.System, type.Table, RecordOf(type, row)) { ReadAt = readAt, Projection = projection };
        return new Answered([.. read.Select(field => new Claim(entity, field, row.Values[type.Fields[field]], [source], [.. hops])
        {
            FieldKind = type.Kinds?.TryGetValue(field, out var kind) == true ? kind : null,
        })]);
    }

    private static string RecordOf(EntityTypeDeclaration type, SourceRow row) =>
        type.Record is null ? row.Key : string.Join('/', type.Record.Select(column => Text(row.Values[column]) ?? ""));

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        _ => value.GetRawText(),
    };

    private static Refused Refuse(int atHop, string? relation, string system, SourceProjection? projection = null) =>
        new(new Refusal(RefusalReason.NoVisibleResult, atHop, relation, system) { Projection = projection });
}
