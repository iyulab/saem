using System.Text.Json;
using Npgsql;
using Saem.Query;

namespace Saem.Connectors.Postgres;

/// <summary>
/// One person's session to a PostgreSQL source: one connection, in one read-only transaction that
/// is rolled back when the session ends. A write cannot happen here even if a grant would allow it —
/// the transaction refuses it at the source.
/// </summary>
internal sealed class PostgresSourceSession : ISourceSession
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    private PostgresSourceSession(NpgsqlDataSource dataSource, NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _dataSource = dataSource;
        _connection = connection;
        _transaction = transaction;
    }

    public static async Task<ISourceSession> OpenAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            {
                await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return new PostgresSourceSession(dataSource, connection, transaction);
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

        await using var command = new NpgsqlCommand(SelectByKey(table, keyColumn, columns), _connection, _transaction);
        command.Parameters.AddWithValue("key", key);

        var rows = new List<SourceRow>();
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

            rows.Add(new SourceRow(reader.GetString(0), values));
        }

        return rows;
    }

    /// <summary>
    /// <c>SELECT key::text, columns… FROM table WHERE key::text = @key</c>. The names come from the
    /// ontology, not from a caller, and are quoted as identifiers all the same; the key is compared as
    /// text because a lookup carries keys as text whatever the column's type.
    /// </summary>
    internal static string SelectByKey(string table, string keyColumn, IReadOnlyList<string> columns)
    {
        var key = Identifier(keyColumn);
        var select = string.Join(", ", columns.Select(Identifier));
        return $"SELECT {key}::text{(columns.Count == 0 ? "" : ", " + select)} FROM {QualifiedTable(table)} WHERE {key}::text = @key";
    }

    /// <summary>A table name, optionally <c>schema.table</c>, each part quoted.</summary>
    internal static string QualifiedTable(string table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        var dot = table.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? Identifier(table) : $"{Identifier(table[..dot])}.{Identifier(table[(dot + 1)..])}";
    }

    internal static string Identifier(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _transaction.RollbackAsync().ConfigureAwait(false);
        }
        finally
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }
}
