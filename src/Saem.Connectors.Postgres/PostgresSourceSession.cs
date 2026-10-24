using System.Runtime.CompilerServices;
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

            await RefuseUnrestrictedRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            return new PostgresSourceSession(dataSource, connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Refuses a login the source's row-level security does not apply to — a superuser, or a role with
    /// <c>BYPASSRLS</c>: through it the source would hand over every row, not the ones it lets the person
    /// read. Checked on every session, since a role's attributes can change between two of them.
    /// </summary>
    private static async Task RefuseUnrestrictedRoleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT rolname, rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && (reader.GetBoolean(1) || reader.GetBoolean(2)))
        {
            throw new InsecureSourceRoleException(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2));
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
        await foreach (var row in RowsAsync(command, columns, cancellationToken).ConfigureAwait(false))
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
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        await using var command = new NpgsqlCommand(SelectWhere(table, keyColumn, column, columns, limit), _connection, _transaction);
        command.Parameters.AddWithValue("value", value);

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

        await using var command = new NpgsqlCommand(SelectAll(table, keyColumn, columns), _connection, _transaction);
        await foreach (var row in RowsAsync(command, columns, cancellationToken).ConfigureAwait(false))
        {
            yield return row;
        }
    }

    private static async IAsyncEnumerable<SourceRow> RowsAsync(
        NpgsqlCommand command, IReadOnlyList<string> columns, [EnumeratorCancellation] CancellationToken cancellationToken)
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

    /// <summary>
    /// <c>SELECT key::text, columns… FROM table WHERE column::text = @value ORDER BY 1 LIMIT n</c> — the rows a
    /// field's value names, in key order as text.
    /// </summary>
    internal static string SelectWhere(string table, string keyColumn, string column, IReadOnlyList<string> columns, int limit)
    {
        var select = string.Join(", ", columns.Select(Identifier));
        return $"SELECT {Identifier(keyColumn)}::text{(columns.Count == 0 ? "" : ", " + select)} FROM {QualifiedTable(table)} " +
            $"WHERE {Identifier(column)}::text = @value ORDER BY 1 LIMIT {limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary><c>SELECT key::text, columns… FROM table ORDER BY 1</c> — every row, in key order as text.</summary>
    internal static string SelectAll(string table, string keyColumn, IReadOnlyList<string> columns)
    {
        var select = string.Join(", ", columns.Select(Identifier));
        return $"SELECT {Identifier(keyColumn)}::text{(columns.Count == 0 ? "" : ", " + select)} FROM {QualifiedTable(table)} ORDER BY 1";
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
