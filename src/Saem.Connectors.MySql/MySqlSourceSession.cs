using System.Runtime.CompilerServices;
using System.Text.Json;
using MySqlConnector;
using Saem.Query;

namespace Saem.Connectors.MySql;

/// <summary>
/// One person's session to a MySQL source: one connection, in one read-only transaction that is
/// rolled back when the session ends. A write cannot happen here even if a grant would allow it — the
/// transaction refuses it at the source.
/// </summary>
internal sealed class MySqlSourceSession : ISourceSession
{
    private readonly MySqlConnection _connection;

    private MySqlSourceSession(MySqlConnection connection) => _connection = connection;

    public static async Task<ISourceSession> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (var readOnly = new MySqlCommand("START TRANSACTION READ ONLY", connection))
            {
                await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return new MySqlSourceSession(connection);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<SourceRow>> ReadAsync(
        string table,
        string keyColumn,
        string key,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(columns);

        await using var command = new MySqlCommand(SelectByKey(table, keyColumn, columns), _connection);
        command.Parameters.AddWithValue("@key", key);

        var rows = new List<SourceRow>();
        await foreach (var row in RowsAsync(command, columns, cancellationToken).ConfigureAwait(false))
        {
            rows.Add(row);
        }

        return rows;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SourceRow> ScanAsync(
        string table,
        string keyColumn,
        IReadOnlyList<string> columns,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(columns);

        await using var command = new MySqlCommand(SelectAll(table, keyColumn, columns), _connection);
        await foreach (var row in RowsAsync(command, columns, cancellationToken).ConfigureAwait(false))
        {
            yield return row;
        }
    }

    private static async IAsyncEnumerable<SourceRow> RowsAsync(
        MySqlCommand command, IReadOnlyList<string> columns, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new Dictionary<string, JsonElement>(columns.Count, StringComparer.Ordinal);
            for (var i = 0; i < columns.Count; i++)
            {
                values[columns[i]] = await reader.IsDBNullAsync(i + 1, cancellationToken).ConfigureAwait(false)
                    ? JsonSerializer.SerializeToElement<object?>(null)
                    : JsonSerializer.SerializeToElement(reader.GetValue(i + 1));
            }

            yield return new SourceRow(reader.GetString(0), values);
        }
    }

    /// <summary>
    /// <c>SELECT CAST(key AS CHAR), columns… FROM table WHERE CAST(key AS CHAR) = @key</c>. The names come
    /// from the ontology, not from a caller, and are quoted as identifiers all the same; the key is
    /// compared as text because a lookup carries keys as text whatever the column's type.
    /// </summary>
    internal static string SelectByKey(string table, string keyColumn, IReadOnlyList<string> columns)
    {
        var key = $"CAST({Identifier(keyColumn)} AS CHAR)";
        var select = string.Join(", ", columns.Select(Identifier));
        return $"SELECT {key}{(columns.Count == 0 ? "" : ", " + select)} FROM {QualifiedTable(table)} WHERE {key} = @key";
    }

    /// <summary><c>SELECT CAST(key AS CHAR), columns… FROM table ORDER BY 1</c> — every row, in key order as text.</summary>
    internal static string SelectAll(string table, string keyColumn, IReadOnlyList<string> columns)
    {
        var select = string.Join(", ", columns.Select(Identifier));
        return $"SELECT CAST({Identifier(keyColumn)} AS CHAR){(columns.Count == 0 ? "" : ", " + select)} FROM {QualifiedTable(table)} ORDER BY 1";
    }

    /// <summary>A table name, optionally <c>database.table</c>, each part quoted.</summary>
    internal static string QualifiedTable(string table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        var dot = table.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? Identifier(table) : $"{Identifier(table[..dot])}.{Identifier(table[(dot + 1)..])}";
    }

    internal static string Identifier(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`";
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var rollback = new MySqlCommand("ROLLBACK", _connection);
            await rollback.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
