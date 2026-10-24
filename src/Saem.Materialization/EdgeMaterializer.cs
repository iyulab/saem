using System.Runtime.CompilerServices;
using System.Text.Json;
using Saem.Contracts;
using Saem.Ontology;
using Saem.Query;

namespace Saem.Materialization;

/// <summary>
/// One instance edge: an entity that leads, through a relation the ontology declares, to another — and the
/// record the edge was read from. It carries no value of the record but the key it holds: an edge says
/// that the two are related, and who may read the record's fields is the record's own rule.
/// </summary>
/// <param name="From">The IRI of the entity the relation leaves.</param>
/// <param name="Relation">The relation, as the ontology names it.</param>
/// <param name="To">The IRI of the entity it leads to.</param>
/// <param name="Source">The record the edge was read from — the row of the leaving entity's table, cited as a lookup cites it.</param>
public sealed record MaterializedEdge(string From, string Relation, string To, SourceRecord Source);

/// <summary>
/// One row a pass read of a relation's leaving table: the entity it is, and the edge it gives — none when its
/// relation field is empty, so an edge the store held for it no longer stands.
/// </summary>
/// <param name="Relation">The relation, as the ontology names it.</param>
/// <param name="From">The IRI of the entity the row is.</param>
/// <param name="Edge">The edge the row gives; null when it leads nowhere.</param>
public sealed record EdgeRow(string Relation, string From, MaterializedEdge? Edge);

/// <summary>
/// Builds the instance edges of every relation the ontology declares, from what the sessions let their
/// holder read. For each relation it reads the whole table of the type the relation leaves
/// (<see cref="ISourceSession.ScanAsync"/>) and gives one edge per row whose relation field holds a key; a
/// row with that field empty leads nowhere. The sessions are the group's: a row the group may not see is
/// not read, so it leaves no edge — the edges a group gets are the ones its own account reaches.
/// <para>
/// The entity the edge leads to is named, not read: whether that entity's row exists, or may be read by the
/// group, is a question for the type it belongs to, asked when it is looked up.
/// </para>
/// <para>
/// A pass may read a relation's table in part (SD-08): where the leaving type declares the column its source sets
/// when a row changes (<see cref="EntityTypeDeclaration.ChangedAt"/>) and the session can narrow a scan by it
/// (<see cref="IChangedRows"/>), only the rows changed at or after the relation's last latest change are read
/// (<see cref="RowsAsync"/>). Such a pass sees no row deleted, nor one the group came to be unable to see — a whole
/// pass does.
/// </para>
/// </summary>
public sealed class EdgeMaterializer(Ontology.Ontology ontology, EntityIris iris)
{
    /// <summary>The edges of every relation, relation by relation in name order, each relation's in key order.</summary>
    public async IAsyncEnumerable<MaterializedEdge> MaterializeAsync(
        ISourceSessions sessions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var row in RowsAsync(sessions, Whole, cancellationToken).ConfigureAwait(false))
        {
            if (row.Edge is { } edge)
            {
                yield return edge;
            }
        }
    }

    private static readonly IReadOnlyDictionary<string, string> Whole = new Dictionary<string, string>();

    /// <summary>
    /// The latest change of each relation's leaving table that can tell its changed rows — the type declares
    /// <see cref="EntityTypeDeclaration.ChangedAt"/> and the session is <see cref="IChangedRows"/> — by relation, as the
    /// source spells it; a relation whose table cannot, or has no row with a change time, is not among them. Asked
    /// before a pass reads, it is where the next pass narrowed to changed rows reads from.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> LatestChangesAsync(ISourceSessions sessions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var latest = new Dictionary<string, string>(StringComparer.Ordinal);
        var open = new Dictionary<string, ISourceSession>(StringComparer.Ordinal);
        try
        {
            foreach (var relation in ontology.Relations.Values.OrderBy(r => r.Name, StringComparer.Ordinal))
            {
                var from = ontology.EntityTypes[relation.From];
                if (from.ChangedAt is { } column
                    && await SessionAsync(open, sessions, from.System, cancellationToken).ConfigureAwait(false) is IChangedRows changes
                    && await changes.LatestChangeAsync(from.Table, column, cancellationToken).ConfigureAwait(false) is { } at)
                {
                    latest.Add(relation.Name, at);
                }
            }
        }
        finally
        {
            foreach (var session in open.Values)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        return latest;
    }

    /// <summary>
    /// The rows a pass reads, relation by relation in name order, each relation's in key order: of a relation named in
    /// <paramref name="since"/>, only the rows changed at or after that latest change; of every other, the whole table.
    /// </summary>
    public async IAsyncEnumerable<EdgeRow> RowsAsync(
        ISourceSessions sessions,
        IReadOnlyDictionary<string, string> since,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(since);

        var open = new Dictionary<string, ISourceSession>(StringComparer.Ordinal);
        try
        {
            foreach (var relation in ontology.Relations.Values.OrderBy(r => r.Name, StringComparer.Ordinal))
            {
                var from = ontology.EntityTypes[relation.From];
                var to = ontology.EntityTypes[relation.To];
                var via = from.Fields[relation.Via];
                var session = await SessionAsync(open, sessions, from.System, cancellationToken).ConfigureAwait(false);
                var columns = new[] { via }.Concat(from.Record ?? []).Distinct(StringComparer.Ordinal).ToList();
                var rows = since.TryGetValue(relation.Name, out var at) && from.ChangedAt is { } changedAt && session is IChangedRows changes
                    ? changes.ScanChangedAsync(from.Table, from.Key, columns, changedAt, at, cancellationToken)
                    : session.ScanAsync(from.Table, from.Key, columns, cancellationToken);
                await foreach (var row in rows.ConfigureAwait(false))
                {
                    // Each entity is named by its key as its type spells keys, the same spelling a lookup asks by.
                    var entity = iris.For(from.Name, from.Normalize(row.Key));
                    yield return new EdgeRow(relation.Name, entity, Text(row.Values[via]) is not { Length: > 0 } target ? null : new MaterializedEdge(
                        entity,
                        relation.Name,
                        iris.For(relation.To, to.Normalize(target)),
                        new SourceRecord(from.System, from.Table, from.Record is null ? row.Key : string.Join('/', from.Record.Select(c => Text(row.Values[c]) ?? "")))));
                }
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

    private static async ValueTask<ISourceSession> SessionAsync(Dictionary<string, ISourceSession> open, ISourceSessions sessions, string system, CancellationToken cancellationToken)
    {
        if (!open.TryGetValue(system, out var session))
        {
            session = await sessions.OpenAsync(system, cancellationToken).ConfigureAwait(false);
            open.Add(system, session);
        }

        return session;
    }

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        _ => value.GetRawText(),
    };
}
