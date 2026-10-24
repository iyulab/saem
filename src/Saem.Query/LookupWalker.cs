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
/// the person's own reads returned.
/// </para>
/// </summary>
public sealed class LookupWalker
{
    private readonly IOntologyCatalog _catalog;
    private readonly TimeProvider _time;

    /// <summary>Creates a walker over the ontology <paramref name="catalog"/> holds.</summary>
    /// <param name="catalog">The ontology.</param>
    /// <param name="time">The clock a source record's read time is taken from; omitted, the system's.</param>
    public LookupWalker(IOntologyCatalog catalog, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _time = time ?? TimeProvider.System;
    }

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
    /// <exception cref="InvalidOperationException">A source returned more than one row for a declared key.</exception>
    public async Task<LookupAnswer> RunAsync(LookupCall call, ISourceSessions sessions, IMaterializedRelations relations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(relations);

        var ontology = await _catalog.GetAsync(cancellationToken).ConfigureAwait(false);
        var plan = ontology.Plan(call);

        var open = new Dictionary<string, ISourceSession>(StringComparer.Ordinal);
        try
        {
            var at = plan.Start;
            var key = call.Start.Key;
            var hops = new List<Hop>();

            for (var hop = 0; ; hop++)
            {
                var next = hop < plan.Steps.Count ? plan.Steps[hop] : null;
                if (next is null && plan.Inverse is { } inverse)
                {
                    // The entity reached is not read: what is asked is the entities that lead to it.
                    return await BackwardsAsync(open, sessions, relations, inverse, new EntityKey(at.Name, key), plan, hops, _time, cancellationToken).ConfigureAwait(false);
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
    /// kept when the source shows the row and the field still names <paramref name="to"/>, so an edge built
    /// before the source changed, or a row this person may not see, adds nothing.
    /// </summary>
    private static async Task<LookupAnswer> BackwardsAsync(
        Dictionary<string, ISourceSession> open,
        ISourceSessions sessions,
        IMaterializedRelations relations,
        RelationDeclaration relation,
        EntityKey to,
        LookupPlan plan,
        List<Hop> hops,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var from = plan.End;
        var atHop = plan.Steps.Count + 1;
        if (relations.Partitions.Count == 0)
        {
            return new Refused(new Refusal(RefusalReason.NoMaterializedPartition, atHop, relation.Inverse, from.System));
        }

        var claims = new List<Claim>();
        foreach (var partition in relations.Partitions)
        {
            await foreach (var key in relations.LeadingToAsync(partition, relation, to.Key, cancellationToken).ConfigureAwait(false))
            {
                var row = await ReadAsync(open, sessions, from, key, [.. plan.Read, relation.Via], cancellationToken).ConfigureAwait(false);
                if (row is null || Text(row.Values[from.Fields[relation.Via]]) != to.Key)
                {
                    continue;
                }

                var back = new Hop(relation.Inverse!, to, new EntityKey(from.Name, row.Key)) { Partition = partition };
                claims.AddRange(Answer(from, row, time.GetUtcNow(), plan.Read, [.. hops, back]).Claims);
            }
        }

        return claims.Count > 0 ? new Answered(claims) : Refuse(atHop, relation.Inverse, from.System);
    }

    private static async Task<SourceRow?> ReadAsync(
        Dictionary<string, ISourceSession> open,
        ISourceSessions sessions,
        EntityTypeDeclaration type,
        string key,
        IReadOnlyList<string> fields,
        CancellationToken cancellationToken)
    {
        if (!open.TryGetValue(type.System, out var session))
        {
            session = await sessions.OpenAsync(type.System, cancellationToken).ConfigureAwait(false);
            open.Add(type.System, session);
        }

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
