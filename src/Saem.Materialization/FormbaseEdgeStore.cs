using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Saem.Contracts;

namespace Saem.Materialization;

/// <summary>
/// Keeps a group's instance edges in that group's own Formbase, over its HTTP surface: one form type,
/// <see cref="FormType"/>, whose declaration has the edge's six text fields and nothing else — there is no
/// field a record's body could be written into, so the store cannot come to hold one.
/// <para>
/// A pass replaces what the store holds with the edges it is given. Each edge is the record keyed by its
/// relation and the entity it leaves (a row leads through a relation to one entity at most): an edge that is
/// already held unchanged is not sent again, a changed one is sent as a correction of its record, and a held
/// edge the pass did not give is retired. The documents stay in the stream, as Formbase keeps every
/// correction — the projection shows the edges of the latest pass.
/// </para>
/// <para>
/// A pass also files when it began reading the sources — one record of a second form type,
/// <see cref="PassFormType"/> — once its edges are in: the edges are the sources as read from then on, so a
/// row a source gained after that time may have no edge yet (<see cref="AsOfAsync"/>).
/// </para>
/// <para>
/// A pass may have read some relations in part — only the rows changed since the last one (SD-08,
/// <see cref="MergeAsync"/>): of those, the rows it read correct their edges and the rest stand. It files, per
/// relation, the latest change it read from (a third form type, <see cref="WatermarkFormType"/>), which the next
/// such pass reads on from (<see cref="WatermarksAsync"/>).
/// </para>
/// <para>
/// Which group the store is is the host's: the client reaches a Formbase started with that group's project
/// and the secret confined to it, so a write meant for another group has no way there.
/// </para>
/// </summary>
/// <param name="http">A client whose <see cref="HttpClient.BaseAddress"/> is the group's Formbase. The caller owns its lifetime.</param>
/// <param name="formbaseNamespace">The Formbase namespace to address, sent as <c>Formbase-Namespace</c>; omitted, the instance's own.</param>
/// <param name="time">The clock a pass's start is taken from; omitted, the system's.</param>
public sealed class FormbaseEdgeStore(HttpClient http, string? formbaseNamespace = null, TimeProvider? time = null)
{
    /// <summary>The form type the edges are filed under.</summary>
    public const string FormType = "edges";

    /// <summary>The table the edges project into.</summary>
    public const string TableName = "saem_edges";

    /// <summary>The form type the latest pass is filed under — one record, <see cref="LatestPass"/>.</summary>
    public const string PassFormType = "edge_passes";

    /// <summary>The table the latest pass projects into.</summary>
    public const string PassTableName = "saem_edge_passes";

    /// <summary>The form type each relation's latest change is filed under, one record per relation.</summary>
    public const string WatermarkFormType = "edge_watermarks";

    /// <summary>The table the latest changes project into.</summary>
    public const string WatermarkTableName = "saem_edge_watermarks";

    private const string LatestPass = "latest";

    private static readonly string[] WatermarkFields = ["relation", "latest_change"];

    private static readonly IReadOnlySet<string> NoneInPart = new HashSet<string>();

    private static readonly IReadOnlyDictionary<string, string> NoWatermarks = new Dictionary<string, string>();

    private static readonly string[] Fields = ["from_iri", "relation", "to_iri", "source_system", "source_table", "source_record"];

    private static readonly string[] PassFields = ["read_from"];

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private readonly FormbaseForms _forms = new(http, formbaseNamespace, message => new EdgeStoreException(message));

    /// <summary>Makes the store hold exactly <paramref name="edges"/>, and projects them.</summary>
    public Task<EdgeWrite> ReplaceAsync(IAsyncEnumerable<MaterializedEdge> edges, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edges);
        return MergeAsync(RowsOf(edges, cancellationToken), NoneInPart, NoWatermarks, cancellationToken);
    }

    private static async IAsyncEnumerable<EdgeRow> RowsOf(IAsyncEnumerable<MaterializedEdge> edges, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var edge in edges.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return new EdgeRow(edge.Relation, edge.From, edge);
        }
    }

    /// <summary>
    /// Makes the store hold what a pass read (<see cref="EdgeMaterializer.RowsAsync"/>) and projects it. A row that gives an
    /// edge files it (unless held unchanged); a row that gives none retires the edge held for its entity. Of a relation in
    /// <paramref name="readInPart"/> — read only where its rows changed — no other edge is touched; of every other, a held
    /// edge no row gave is retired, as a whole pass does. <paramref name="latestChanges"/> are filed as where the next pass
    /// narrowed to changed rows reads each relation from; a relation not among them is read whole by it.
    /// </summary>
    public async Task<EdgeWrite> MergeAsync(
        IAsyncEnumerable<EdgeRow> rows,
        IReadOnlySet<string> readInPart,
        IReadOnlyDictionary<string, string> latestChanges,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(readInPart);
        ArgumentNullException.ThrowIfNull(latestChanges);

        await _forms.DeclareAsync(FormType, TableName, Fields, cancellationToken).ConfigureAwait(false);
        var held = await _forms.HeldAsync(FormType, "", Fields, cancellationToken).ConfigureAwait(false);

        // The edges are read from the sources as they are enumerated, from here on.
        var readFrom = _time.GetUtcNow();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int written = 0, unchanged = 0, retired = 0, read = 0;
        await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            read++;
            var key = row.Relation + " " + row.From;
            if (!seen.Add(key))
            {
                throw new EdgeStoreException($"Two edges of relation '{row.Relation}' leave '{row.From}'; an entity leads through a relation to one entity at most.");
            }

            if (row.Edge is not { } edge)
            {
                if (held.ContainsKey(key))
                {
                    await _forms.RetireAsync(FormType, key, "Retiring an edge", cancellationToken).ConfigureAwait(false);
                    retired++;
                }

                continue;
            }

            var fields = FieldsOf(edge);
            if (held.TryGetValue(key, out var current) && current.SequenceEqual(fields))
            {
                unchanged++;
                continue;
            }

            await _forms.FileAsync(FormType, key, Fields, fields, "Filing an edge", cancellationToken).ConfigureAwait(false);
            written++;
        }

        // A relation read whole gave every edge it holds; one read in part said nothing of the rows it did not read.
        foreach (var (key, _) in held.Where(h => !seen.Contains(h.Key) && !readInPart.Contains(h.Value[1])).OrderBy(h => h.Key, StringComparer.Ordinal))
        {
            await _forms.RetireAsync(FormType, key, "Retiring an edge", cancellationToken).ConfigureAwait(false);
            retired++;
        }

        await _forms.ProjectAsync(FormType, "Projecting the edges", cancellationToken).ConfigureAwait(false);

        await _forms.DeclareAsync(WatermarkFormType, WatermarkTableName, WatermarkFields, cancellationToken).ConfigureAwait(false);
        var heldMarks = await _forms.HeldAsync(WatermarkFormType, "", WatermarkFields, cancellationToken).ConfigureAwait(false);
        foreach (var (relation, latest) in latestChanges.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            if (!(heldMarks.TryGetValue(relation, out var mark) && mark[1] == latest))
            {
                await _forms.FileAsync(WatermarkFormType, relation, WatermarkFields, [relation, latest], "Filing a latest change", cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var relation in heldMarks.Keys.Where(r => !latestChanges.ContainsKey(r)).Order(StringComparer.Ordinal))
        {
            await _forms.RetireAsync(WatermarkFormType, relation, "Retiring a latest change", cancellationToken).ConfigureAwait(false);
        }

        await _forms.ProjectAsync(WatermarkFormType, "Projecting the latest changes", cancellationToken).ConfigureAwait(false);

        // Filed only once the edges are in: a pass that failed leaves the time of the last one that did not.
        await _forms.DeclareAsync(PassFormType, PassTableName, PassFields, cancellationToken).ConfigureAwait(false);
        await _forms.FileAsync(PassFormType, LatestPass, PassFields, [readFrom.ToString("O", CultureInfo.InvariantCulture)], "Filing the pass", cancellationToken).ConfigureAwait(false);
        await _forms.ProjectAsync(PassFormType, "Projecting the pass", cancellationToken).ConfigureAwait(false);

        return new EdgeWrite(written, unchanged, retired) { ReadFrom = readFrom, RowsRead = read, ReadInPart = readInPart.Count };
    }

    /// <summary>
    /// Where the last pass left each relation's reading: the latest change it read from, by relation, as the source spells
    /// it — none for a relation it read whole without one, nor before a pass filed any.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> WatermarksAsync(CancellationToken cancellationToken = default)
    {
        var held = await _forms.HeldAsync(WatermarkFormType, "", WatermarkFields, cancellationToken).ConfigureAwait(false);
        return held.ToDictionary(m => m.Key, m => m.Value[1], StringComparer.Ordinal);
    }

    /// <summary>
    /// Compares what the store holds with <paramref name="edges"/> — the edges a pass would give now — and writes
    /// nothing: how many it holds as given, how many it lacks, holds otherwise, or holds that are no longer given,
    /// and how many documents its projection skipped. A store that agrees holds exactly what a pass would make it
    /// hold. Counts only: no edge is named, since an edge names entities by their keys.
    /// </summary>
    public async Task<EdgeComparison> CompareAsync(IAsyncEnumerable<MaterializedEdge> edges, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edges);

        var skipped = 0;
        var held = await _forms.HeldAsync(FormType, "", Fields, cancellationToken, readOn: count => skipped = count).ConfigureAwait(false);
        var given = new HashSet<string>(StringComparer.Ordinal);
        int matching = 0, missing = 0, differing = 0;
        await foreach (var edge in edges.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var key = KeyOf(edge);
            if (!given.Add(key))
            {
                throw new EdgeStoreException($"Two edges of relation '{edge.Relation}' leave '{edge.From}'; an entity leads through a relation to one entity at most.");
            }

            if (!held.TryGetValue(key, out var current))
            {
                missing++;
            }
            else if (current.SequenceEqual(FieldsOf(edge)))
            {
                matching++;
            }
            else
            {
                differing++;
            }
        }

        return new EdgeComparison(matching, missing, differing, held.Keys.Count(k => !given.Contains(k)), skipped);
    }

    /// <summary>
    /// When the latest pass began reading the sources — the time the held edges are as of: an edge a source
    /// row gained after it may be missing. Null before a pass has filed one (a store a pass of an earlier
    /// version wrote has edges and no time).
    /// </summary>
    public async Task<DateTimeOffset?> AsOfAsync(CancellationToken cancellationToken = default)
    {
        // Read for a lookup, so a skipped pass record reads as no time rather than failing the lookup.
        var held = await _forms.HeldAsync(PassFormType, "", PassFields, cancellationToken, readOn: _ => { }).ConfigureAwait(false);
        if (!held.TryGetValue(LatestPass, out var fields))
        {
            return null;
        }

        return DateTimeOffset.TryParseExact(fields[0], "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var readFrom)
            ? readFrom
            : throw new EdgeStoreException($"The latest pass names its start as '{fields[0]}', which is not a round-trip time.");
    }

    /// <summary>The edges the store holds now, in key order — all of them, or a failure when the projection skipped documents.</summary>
    public async IAsyncEnumerable<MaterializedEdge> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var edge in Edges(await _forms.HeldAsync(FormType, "", Fields, cancellationToken).ConfigureAwait(false)))
        {
            yield return edge;
        }
    }

    /// <summary>
    /// The held edges of <paramref name="relation"/> that lead to the entity named <paramref name="to"/> —
    /// the relation walked backwards — in the order of the entities they leave, at most
    /// <paramref name="limit"/> and one more: one more says there are more than the limit, without reading them all.
    /// Asked of the store, so only the matching edges are read.
    /// <para>
    /// A projection that skipped documents is read over the rows that stand, and the read says so
    /// (<see cref="EdgesLeadingTo.Incomplete"/>). An edge is only a candidate a lookup reads again as the person,
    /// so an edge missing from a skipped document can only leave an answer out, as a pass that has not run yet
    /// does; it cannot put a wrong one in. A pass never reads on: what it does not see it cannot retire.
    /// </para>
    /// </summary>
    public async Task<EdgesLeadingTo> LeadingToAsync(string relation, string to, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(relation);
        ArgumentException.ThrowIfNullOrEmpty(to);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        var skipped = 0;
        var held = await _forms.HeldAsync(
            FormType,
            $"&filter={Uri.EscapeDataString("relation:" + relation)}&filter={Uri.EscapeDataString("to_iri:" + to)}",
            Fields,
            cancellationToken,
            readOn: count => skipped = count,
            atMost: limit + 1).ConfigureAwait(false);
        return new EdgesLeadingTo([.. Edges(held)], skipped);
    }

    private static IEnumerable<MaterializedEdge> Edges(Dictionary<string, string[]> held) =>
        held.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new MaterializedEdge(p.Value[0], p.Value[1], p.Value[2], new SourceRecord(p.Value[3], p.Value[4], p.Value[5])));

    private static string KeyOf(MaterializedEdge edge) => edge.Relation + " " + edge.From;

    private static string[] FieldsOf(MaterializedEdge edge) =>
        [edge.From, edge.Relation, edge.To, edge.Source.System, edge.Source.Table, edge.Source.Record];

}

/// <summary>What a pass did to the store.</summary>
/// <param name="Written">Edges sent — new ones, and held ones whose entity now leads elsewhere or is cited from another record.</param>
/// <param name="Unchanged">Edges the store already held as given, not sent again.</param>
/// <param name="Retired">Held edges the pass did not give, taken out.</param>
public sealed record EdgeWrite(int Written, int Unchanged, int Retired)
{
    /// <summary>When the pass began reading the sources — what the edges are as of.</summary>
    public DateTimeOffset? ReadFrom { get; init; }

    /// <summary>How many rows of the sources the pass read.</summary>
    public int RowsRead { get; init; }

    /// <summary>How many relations the pass read only where their rows changed.</summary>
    public int ReadInPart { get; init; }
}

/// <summary>How a store's edges compare with the ones a pass would give now.</summary>
/// <param name="Matching">Edges the store holds as given.</param>
/// <param name="Missing">Given edges the store does not hold.</param>
/// <param name="Differing">Edges the store holds otherwise — leading elsewhere, or cited from another record.</param>
/// <param name="Extra">Edges the store holds that are no longer given.</param>
/// <param name="SkippedDocuments">Documents the store's projection skipped — edges it holds but cannot show.</param>
public sealed record EdgeComparison(int Matching, int Missing, int Differing, int Extra, int SkippedDocuments)
{
    /// <summary>The store holds exactly what a pass would make it hold.</summary>
    public bool Agrees => Missing == 0 && Differing == 0 && Extra == 0 && SkippedDocuments == 0;
}

/// <summary>The edges a read for a lookup found leading to an entity, and whether the store holds them all.</summary>
/// <param name="Edges">The edges, in the order of the entities they leave.</param>
/// <param name="SkippedDocuments">How many documents the projection skipped; 0 when it holds every edge it was given.</param>
public sealed record EdgesLeadingTo(IReadOnlyList<MaterializedEdge> Edges, int SkippedDocuments)
{
    /// <summary>The projection skipped documents, so edges may be missing.</summary>
    public bool Incomplete => SkippedDocuments > 0;
}

/// <summary>The group's edge store refused a pass, or holds what saem did not write.</summary>
public sealed class EdgeStoreException : Exception
{
    public EdgeStoreException()
    {
    }

    public EdgeStoreException(string message)
        : base(message)
    {
    }

    public EdgeStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
