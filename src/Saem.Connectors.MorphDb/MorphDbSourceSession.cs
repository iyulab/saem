using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Saem.Query;

namespace Saem.Connectors.MorphDb;

/// <summary>
/// One person's session to a MorphDB source: every request presents their token, and only reads are
/// sent. MorphDB's row-level security, evaluated as that person, decides which rows come back — a row
/// they may not see is not returned, exactly as a row that does not exist.
/// </summary>
internal sealed class MorphDbSourceSession : ISourceSession
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
        for (var page = 1; ; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReadByKey(table, keyColumn, key, columns, page));
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

                rows.Add(new SourceRow(AsText(data.GetProperty(keyColumn)), values));
            }

            if (!body.RootElement.GetProperty("pagination").TryGetProperty("hasNext", out var hasNext) || !hasNext.GetBoolean())
            {
                return rows;
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

    private static string AsText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => value.GetRawText(),
    };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
