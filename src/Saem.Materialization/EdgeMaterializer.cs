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
/// Builds the instance edges of every relation the ontology declares, from what the sessions let their
/// holder read. For each relation it reads the whole table of the type the relation leaves
/// (<see cref="ISourceSession.ScanAsync"/>) and gives one edge per row whose relation field holds a key; a
/// row with that field empty leads nowhere. The sessions are the group's: a row the group may not see is
/// not read, so it leaves no edge — the edges a group gets are the ones its own account reaches.
/// <para>
/// The entity the edge leads to is named, not read: whether that entity's row exists, or may be read by the
/// group, is a question for the type it belongs to, asked when it is looked up.
/// </para>
/// </summary>
public sealed class EdgeMaterializer(Ontology.Ontology ontology, EntityIris iris)
{
    /// <summary>The edges of every relation, relation by relation in name order, each relation's in key order.</summary>
    public async IAsyncEnumerable<MaterializedEdge> MaterializeAsync(
        ISourceSessions sessions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var open = new Dictionary<string, ISourceSession>(StringComparer.Ordinal);
        try
        {
            foreach (var relation in ontology.Relations.Values.OrderBy(r => r.Name, StringComparer.Ordinal))
            {
                var from = ontology.EntityTypes[relation.From];
                var to = ontology.EntityTypes[relation.To];
                var via = from.Fields[relation.Via];
                if (!open.TryGetValue(from.System, out var session))
                {
                    session = await sessions.OpenAsync(from.System, cancellationToken).ConfigureAwait(false);
                    open.Add(from.System, session);
                }

                var columns = new[] { via }.Concat(from.Record ?? []).Distinct(StringComparer.Ordinal).ToList();
                await foreach (var row in session.ScanAsync(from.Table, from.Key, columns, cancellationToken).ConfigureAwait(false))
                {
                    if (Text(row.Values[via]) is not { Length: > 0 } target)
                    {
                        continue;
                    }

                    // Each entity is named by its key as its type spells keys, the same spelling a lookup asks by.
                    yield return new MaterializedEdge(
                        iris.For(from.Name, from.Normalize(row.Key)),
                        relation.Name,
                        iris.For(relation.To, to.Normalize(target)),
                        new SourceRecord(from.System, from.Table, from.Record is null ? row.Key : string.Join('/', from.Record.Select(c => Text(row.Values[c]) ?? ""))));
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

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        _ => value.GetRawText(),
    };
}
