using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Saem.Query;

namespace Saem.Connectors.MorphDb;

/// <summary>
/// One person's session to a MorphDB source: every request presents their token, and only reads are
/// sent. MorphDB's row-level security, evaluated as that person, decides which rows come back — a row
/// they may not see is not returned, exactly as a row that does not exist.
/// </summary>
internal sealed class MorphDbSourceSession : ISourceSession, IChangedRows
{
    /// <summary>Rows asked for per page; the largest the service serves.</summary>
    internal const int PageSize = 1000;

    private readonly HttpClient _http;
    private readonly Guid _projectId;
    private readonly string _token;

    public MorphDbSourceSession(HttpClient http, Guid projectId, string token)
    {
        _http = http;
        _projectId = projectId;
        _token = token;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<SourceRow>> ReadAsync(
        string table,
        string keyColumn,
        string key,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyColumn);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(columns);

        var rows = new List<SourceRow>();
        await foreach (var row in PagesAsync(table, keyColumn, columns, page => ReadByKey(table, keyColumn, key, columns, page), cancellationToken).ConfigureAwait(false))
        {
            rows.Add(row);
        }

        return rows;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<SourceRow>> FindAsync(
        string table,
        string keyColumn,
        string column,
        string value,
        IReadOnlyList<string> columns,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyColumn);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        var rows = new List<SourceRow>();
        if (limit == 0)
        {
            return rows;
        }

        var pageSize = Math.Min(limit, PageSize);
        await foreach (var row in PagesAsync(table, keyColumn, columns, page => ReadWhere(table, keyColumn, column, value, columns, page, pageSize), cancellationToken).ConfigureAwait(false))
        {
            rows.Add(row);
            if (rows.Count == limit)
            {
                break;
            }
        }

        return rows;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<SourceRow> ScanAsync(
        string table,
        string keyColumn,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyColumn);
        ArgumentNullException.ThrowIfNull(columns);

        return PagesAsync(table, keyColumn, columns, page => ReadAll(table, keyColumn, columns, page), cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<string?> LatestChangeAsync(string table, string column, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);

        // The first row of the latest-first order is the latest; the pages after it are never asked.
        await foreach (var row in PagesAsync(table, column, [column], page => ReadLatest(table, column, page), cancellationToken).ConfigureAwait(false))
        {
            return row.Key;
        }

        return null;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<SourceRow> ScanChangedAsync(
        string table,
        string keyColumn,
        IReadOnlyList<string> columns,
        string column,
        string since,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyColumn);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(since);

        return PagesAsync(table, keyColumn, columns, page => ReadChanged(table, keyColumn, columns, column, since, page), cancellationToken);
    }

    private async IAsyncEnumerable<SourceRow> PagesAsync(
        string table,
        string keyColumn,
        IReadOnlyList<string> columns,
        Func<int, string> pageUri,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var page = 1; ; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, pageUri(page));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Add("X-Project-Id", _projectId.ToString());

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new MorphDbSourceException(
                    $"The source answered {(int)response.StatusCode} reading '{table}'.", response.StatusCode);
            }

            using var body = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var record in body.RootElement.GetProperty("data").EnumerateArray())
            {
                var data = record.GetProperty("data");
                var values = new Dictionary<string, JsonElement>(columns.Count, StringComparer.Ordinal);
                foreach (var column in columns)
                {
                    values[column] = data.TryGetProperty(column, out var value)
                        ? value.Clone()
                        : JsonSerializer.SerializeToElement<object?>(null);
                }

                yield return new SourceRow(AsText(data.GetProperty(keyColumn)), values);
            }

            if (!body.RootElement.GetProperty("pagination").TryGetProperty("hasNext", out var hasNext) || !hasNext.GetBoolean())
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// <c>api/data/{table}?filter={key}:eq:{value}&amp;select={key},{columns…}&amp;page=…&amp;pageSize=…</c> —
    /// relative to the source's base address. Every part is escaped; the filter separates on its first
    /// two colons only, so a key that contains one is compared whole.
    /// </summary>
    internal static string ReadByKey(string table, string keyColumn, string key, IReadOnlyList<string> columns, int page)
    {
        var select = string.Join(',', new[] { keyColumn }.Concat(columns.Where(c => c != keyColumn)));
        return string.Create(CultureInfo.InvariantCulture,
            $"api/data/{Uri.EscapeDataString(table)}?filter={Uri.EscapeDataString($"{keyColumn}:eq:{key}")}" +
            $"&select={Uri.EscapeDataString(select)}&page={page}&pageSize={PageSize}");
    }

    /// <summary>
    /// <c>api/data/{table}?filter={column}:eq:{value}&amp;select={key},{columns…}&amp;orderBy={key}:asc&amp;page=…&amp;pageSize=…</c>
    /// — the rows a field's value names, in key order, a page at a time.
    /// </summary>
    internal static string ReadWhere(string table, string keyColumn, string column, string value, IReadOnlyList<string> columns, int page, int pageSize)
    {
        var select = string.Join(',', new[] { keyColumn }.Concat(columns.Where(c => c != keyColumn)));
        return string.Create(CultureInfo.InvariantCulture,
            $"api/data/{Uri.EscapeDataString(table)}?filter={Uri.EscapeDataString($"{column}:eq:{value}")}" +
            $"&select={Uri.EscapeDataString(select)}&orderBy={Uri.EscapeDataString($"{keyColumn}:asc")}&page={page}&pageSize={pageSize}");
    }

    /// <summary>
    /// <c>api/data/{table}?select={key},{columns…}&amp;orderBy={key}:asc&amp;page=…&amp;pageSize=…</c> — every row,
    /// in key order; the service ends every page's order with <c>_id</c>, so consecutive pages neither
    /// repeat nor skip a row.
    /// </summary>
    internal static string ReadAll(string table, string keyColumn, IReadOnlyList<string> columns, int page)
    {
        var select = string.Join(',', new[] { keyColumn }.Concat(columns.Where(c => c != keyColumn)));
        return string.Create(CultureInfo.InvariantCulture,
            $"api/data/{Uri.EscapeDataString(table)}?select={Uri.EscapeDataString(select)}" +
            $"&orderBy={Uri.EscapeDataString($"{keyColumn}:asc")}&page={page}&pageSize={PageSize}");
    }

    /// <summary>
    /// <c>api/data/{table}?filter={column}:isnotnull&amp;select={column}&amp;orderBy={column}:desc&amp;page=…&amp;pageSize=1</c> —
    /// the latest value of the column, one row a page; a row that leaves it empty is not a change.
    /// </summary>
    internal static string ReadLatest(string table, string column, int page) =>
        string.Create(CultureInfo.InvariantCulture,
            $"api/data/{Uri.EscapeDataString(table)}?filter={Uri.EscapeDataString($"{column}:isnotnull")}" +
            $"&select={Uri.EscapeDataString(column)}&orderBy={Uri.EscapeDataString($"{column}:desc")}&page={page}&pageSize=1");

    /// <summary>
    /// <c>api/data/{table}?filter={column}:gte:{since}&amp;select={key},{columns…}&amp;orderBy={key}:asc&amp;page=…&amp;pageSize=…</c> —
    /// the rows changed at or after a latest change, in key order.
    /// </summary>
    internal static string ReadChanged(string table, string keyColumn, IReadOnlyList<string> columns, string column, string since, int page)
    {
        var select = string.Join(',', new[] { keyColumn }.Concat(columns.Where(c => c != keyColumn)));
        return string.Create(CultureInfo.InvariantCulture,
            $"api/data/{Uri.EscapeDataString(table)}?filter={Uri.EscapeDataString($"{column}:gte:{since}")}" +
            $"&select={Uri.EscapeDataString(select)}&orderBy={Uri.EscapeDataString($"{keyColumn}:asc")}&page={page}&pageSize={PageSize}");
    }

    private static string AsText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => value.GetRawText(),
    };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
