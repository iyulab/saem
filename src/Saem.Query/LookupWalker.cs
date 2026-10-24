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
/// A last step that walks a relation backwards asks the person's materialized partitions which entities
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

    /// <summary>Creates a walker over the ontology <paramref name="catalog"/> holds.</summary>
    /// <param name="catalog">The ontology.</param>
    /// <param name="time">The clock a source record's read time is taken from; omitted, the system's.</param>
    /// <param name="maxCandidatesPerStep">The most candidates one step may read; omitted, <see cref="DefaultMaxCandidatesPerStep"/>.</param>
    public LookupWalker(IOntologyCatalog catalog, TimeProvider? time = null, int maxCandidatesPerStep = DefaultMaxCandidatesPerStep)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCandidatesPerStep, 1);
        _catalog = catalog;
        _time = time ?? TimeProvider.System;
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
                    return Refuse(0, null, at.System);
                }

                if (found.Count > MaxCandidatesPerStep)
                {
                    throw TooBroadException.AtStart(at.Name, where.Field, MaxCandidatesPerStep);
                }

                if (found.Count > 1)
                {
                    var readAt = _time.GetUtcNow();
                    return new Candidates([.. found.Select(row => new Candidate(
                        new EntityKey(at.Name, row.Key), new SourceRecord(at.System, at.Table, RecordOf(at, row)) { ReadAt = readAt }))]);
                }

                key = found[0].Key;
            }
            else
            {
                key = call.Start.Key!;
            }

            var hops = new List<Hop>();

            for (var hop = 0; ; hop++)
            {
                var next = hop < plan.Steps.Count ? plan.Steps[hop] : null;
                if (next is null && plan.Inverse is { } inverse)
                {
                    // The entity reached is not read: what is asked is the entities that lead to it.
                    return await BackwardsAsync(open, sessions, relations, inverse, new EntityKey(at.Name, key), plan, hops, cancellationToken).ConfigureAwait(false);
                }

                var fields = next is null ? plan.Read : [next.Via];
                var row = await ReadAsync(open, sessions, at, key, fields, cancellationToken).ConfigureAwait(false);
                if (row is null)
                {
                    return Refuse(hop, hop == 0 ? null : plan.Steps[hop - 1].Name, at.System);
                }

                if (next is null)
                {
                    return Answer(at, row, _time.GetUtcNow(), plan.Read, hops);
                }

                var to = ontology.EntityTypes[next.To];
                var via = row.Values[at.Fields[next.Via]];
                if (via.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    // The field that names the next entity is empty: there is nothing to follow, which
                    // the answer cannot tell apart from a next entity the person may not see.
                    return Refuse(hop + 1, next.Name, to.System);
                }

                var nextKey = via.ValueKind == JsonValueKind.String ? via.GetString()! : via.GetRawText();
                hops.Add(new Hop(next.Name, new EntityKey(at.Name, row.Key), new EntityKey(to.Name, nextKey)));
                at = to;
                key = nextKey;
            }
        }
        finally
        {
            foreach (var session in open.Values)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The last step, walked backwards: in each partition, the entities whose relation leads to
    /// <paramref name="to"/>, each read as the person with the asked fields and the relation's own field —
    /// kept when the source shows the row and the field still names <paramref name="to"/>. An edge built
    /// before the source changed, or one to a row this person may not see, adds nothing to the facts; the
    /// partition is then reported blocked, which says that much and no more.
    /// </summary>
    private async Task<LookupAnswer> BackwardsAsync(
        Dictionary<string, ISourceSession> open,
        ISourceSessions sessions,
        IMaterializedRelations relations,
        RelationDeclaration relation,
        EntityKey to,
        LookupPlan plan,
        List<Hop> hops,
        CancellationToken cancellationToken)
    {
        var from = plan.End;
        var atHop = plan.Steps.Count + 1;
        if (relations.Partitions.Count == 0)
        {
            return new Refused(new Refusal(RefusalReason.NoMaterializedPartition, atHop, relation.Inverse, from.System));
        }

        // Every partition's candidates are counted before any is read: a step too broad in one partition reads none.
        var leading = new List<(string Partition, PartitionEdges Edges)>();
        foreach (var partition in relations.Partitions)
        {
            var edges = await relations.LeadingToAsync(partition, relation, to.Key, MaxCandidatesPerStep, cancellationToken).ConfigureAwait(false);
            if (edges.Keys.Count > MaxCandidatesPerStep)
            {
                throw new TooBroadException(atHop, relation.Inverse!, MaxCandidatesPerStep);
            }

            leading.Add((partition, edges));
        }

        var claims = new List<Claim>();
        var reads = new List<PartitionRead>();
        foreach (var (partition, edges) in leading)
        {
            if (edges.Keys.Count == 0 && !edges.Incomplete)
            {
                // Led nowhere and holds everything it was given: nothing to say of it.
                continue;
            }

            // Asked only of a partition that has something to report.
            var asOf = await relations.AsOfAsync(partition, cancellationToken).ConfigureAwait(false);
            var blocked = false;
            foreach (var key in edges.Keys)
            {
                var row = await ReadAsync(open, sessions, from, key, [.. plan.Read, relation.Via], cancellationToken).ConfigureAwait(false);
                if (row is null || Text(row.Values[from.Fields[relation.Via]]) != to.Key)
                {
                    blocked = true;
                    continue;
                }

                var back = new Hop(relation.Inverse!, to, new EntityKey(from.Name, row.Key)) { Partition = partition, PartitionAsOf = asOf };
                claims.AddRange(Answer(from, row, _time.GetUtcNow(), plan.Read, [.. hops, back]).Claims);
            }

            reads.Add(new PartitionRead(partition, asOf, blocked, edges.Incomplete));
        }

        LookupAnswer answer = claims.Count > 0 ? new Answered(claims) : Refuse(atHop, relation.Inverse, from.System);
        return reads.Count > 0 ? answer with { Partitions = reads } : answer;
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
    private static Answered Answer(EntityTypeDeclaration type, SourceRow row, DateTimeOffset readAt, IReadOnlyList<string> read, IReadOnlyList<Hop> hops)
    {
        var entity = new EntityKey(type.Name, row.Key);
        var source = new SourceRecord(type.System, type.Table, RecordOf(type, row)) { ReadAt = readAt };
        return new Answered([.. read.Select(field => new Claim(entity, field, row.Values[type.Fields[field]], [source], [.. hops]))]);
    }

    private static string RecordOf(EntityTypeDeclaration type, SourceRow row) =>
        type.Record is null ? row.Key : string.Join('/', type.Record.Select(column => Text(row.Values[column]) ?? ""));

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        _ => value.GetRawText(),
    };

    private static Refused Refuse(int atHop, string? relation, string system) =>
        new(new Refusal(RefusalReason.NoVisibleResult, atHop, relation, system));
}
