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
/// </summary>
public sealed class LookupWalker
{
    private readonly IOntologyCatalog _catalog;

    /// <summary>Creates a walker over the ontology <paramref name="catalog"/> holds.</summary>
    public LookupWalker(IOntologyCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    /// <summary>Runs <paramref name="call"/> for the person <paramref name="sessions"/> reads as.</summary>
    /// <exception cref="UnknownTermException">The lookup is not in the ontology's terms; no source was read.</exception>
    /// <exception cref="InvalidOperationException">A source returned more than one row for a declared key.</exception>
    public async Task<LookupAnswer> RunAsync(LookupCall call, ISourceSessions sessions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(sessions);

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
                var fields = next is null ? plan.Read : [next.Via];
                var row = await ReadAsync(open, sessions, at, key, fields, cancellationToken).ConfigureAwait(false);
                if (row is null)
                {
                    return Refuse(hop, hop == 0 ? null : plan.Steps[hop - 1].Name, at.System);
                }

                if (next is null)
                {
                    return Answer(at, row, plan.Read, hops);
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

    private static Answered Answer(EntityTypeDeclaration type, SourceRow row, IReadOnlyList<string> read, List<Hop> hops)
    {
        var entity = new EntityKey(type.Name, row.Key);
        var source = new SourceRecord(type.System, type.Table, RecordOf(type, row));
        return new Answered([.. read.Select(field => new Claim(entity, field, row.Values[type.Fields[field]], [source], [.. hops]))]);
    }

    private static string RecordOf(EntityTypeDeclaration type, SourceRow row) =>
        type.Record is null ? row.Key : string.Join('/', type.Record.Select(column => Text(row.Values[column])));

    private static string Text(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    private static Refused Refuse(int atHop, string? relation, string system) =>
        new(new Refusal(RefusalReason.NoVisibleResult, atHop, relation, system));
}
