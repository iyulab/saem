using System.Text.Json;

namespace Saem.Query;

/// <summary>
/// Opens sessions to the source systems as one person — the one a lookup is run for. An instance is
/// made per lookup with that person's credentials already bound; the walk never sees a credential,
/// and cannot read as anyone else.
/// </summary>
public interface ISourceSessions
{
    /// <summary>Opens a session to <paramref name="system"/> as the person.</summary>
    ValueTask<ISourceSession> OpenAsync(string system, CancellationToken cancellationToken = default);
}

/// <summary>
/// A session to one source system as one person. The source enforces what the person may see: a row
/// they may not see is simply not returned, exactly as a row that does not exist.
/// </summary>
public interface ISourceSession : IAsyncDisposable
{
    /// <summary>
    /// Reads the row of <paramref name="table"/> whose <paramref name="keyColumn"/> equals
    /// <paramref name="key"/>, with the named columns. Returns no row when there is none the person
    /// may see.
    /// </summary>
    ValueTask<IReadOnlyList<SourceRow>> ReadAsync(
        string table,
        string keyColumn,
        string key,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads every row of <paramref name="table"/> the person may see, with the named columns, ordered by
    /// <paramref name="keyColumn"/> (as the source orders it, so two passes meet the rows in the same order) — a pass over a whole table, as the
    /// materialization of a group's edges makes, rather than one row a walk asked for. Rows arrive as
    /// the source sends them; the source's rules decide which, exactly as for <see cref="ReadAsync"/>.
    /// </summary>
    IAsyncEnumerable<SourceRow> ScanAsync(
        string table,
        string keyColumn,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken = default);
}

/// <summary>A row as the source returned it: its key, and the asked columns' values.</summary>
/// <param name="Key">The row's key, as text.</param>
/// <param name="Values">The asked columns, by column name; <see cref="JsonValueKind.Null"/> for an empty one.</param>
public sealed record SourceRow(string Key, IReadOnlyDictionary<string, JsonElement> Values);
