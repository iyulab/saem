using System.Globalization;
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
internal sealed class PostgresSourceSession : ISourceSession, IChangedRows
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly HashSet<string> _rowSecurityChecked = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Table, string Column), bool> _integerColumns = [];

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

    /// <summary>
    /// Refuses to read a table whose row-level security is enabled but not forced when the login owns it — or is a
    /// member of the role that does: PostgreSQL exempts a table's owner from its row-level security unless it is forced,
    /// so the policy would not decide what the session reads. Asked once per table per session, of the catalog, in the
    /// session's own transaction. A table that is not there, or one with no row-level security, is the read's to answer.
    /// </summary>
    private async Task RefuseOwnedUnforcedTableAsync(string table, CancellationToken cancellationToken)
    {
        if (!_rowSecurityChecked.Add(table))
        {
            return;
        }

        await using var command = new NpgsqlCommand(
            "SELECT current_user, c.relrowsecurity AND NOT c.relforcerowsecurity AND pg_has_role(current_user, c.relowner, 'USAGE') "
            + "FROM pg_class c WHERE c.oid = to_regclass(@name) AND c.relkind IN ('r', 'p')", _connection, _transaction);
        command.Parameters.AddWithValue("name", QualifiedTable(table));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.GetBoolean(1))
        {
            throw new InsecureSourceRoleException(reader.GetString(0), table);
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

        await RefuseOwnedUnforcedTableAsync(table, cancellationToken).ConfigureAwait(false);
        NpgsqlCommand command;
        if (await IsIntegerColumnAsync(table, keyColumn, cancellationToken).ConfigureAwait(false))
        {
            // An integer column's text is canonical: a key spelled any other way names no row, and one spelled so is compared as the number.
            if (!TryCanonicalInteger(key, out var number))
            {
                return [];
            }

            command = new NpgsqlCommand(SelectByIntegerKey(table, keyColumn, columns), _connection, _transaction);
            command.Parameters.AddWithValue("key", number);
        }
        else
        {
            command = new NpgsqlCommand(SelectByKey(table, keyColumn, columns), _connection, _transaction);
            command.Parameters.AddWithValue("key", key);
        }

        await using var _ = command.ConfigureAwait(false);

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

        await RefuseOwnedUnforcedTableAsync(table, cancellationToken).ConfigureAwait(false);
        NpgsqlCommand command;
        if (await IsIntegerColumnAsync(table, column, cancellationToken).ConfigureAwait(false))
        {
            if (!TryCanonicalInteger(value, out var number))
            {
                return [];
            }

            command = new NpgsqlCommand(SelectWhereInteger(table, keyColumn, column, columns, limit), _connection, _transaction);
            command.Parameters.AddWithValue("value", number);
        }
        else
        {
            command = new NpgsqlCommand(SelectWhere(table, keyColumn, column, columns, limit), _connection, _transaction);
            command.Parameters.AddWithValue("value", value);
        }

        await using var _ = command.ConfigureAwait(false);

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

        await RefuseOwnedUnforcedTableAsync(table, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(SelectAll(table, keyColumn, columns), _connection, _transaction);
        await foreach (var row in RowsAsync(command, columns, cancellationToken).ConfigureAwait(false))
        {
            yield return row;
        }
    }

    /// <inheritdoc />
    public async ValueTask<string?> LatestChangeAsync(string table, string column, CancellationToken cancellationToken = default)
    {
        await RefuseOwnedUnforcedTableAsync(table, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(SelectLatest(table, column), _connection, _transaction);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SourceRow> ScanChangedAsync(
        string table,
        string keyColumn,
        IReadOnlyList<string> columns,
        string column,
        string since,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(since);

        await RefuseOwnedUnforcedTableAsync(table, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(SelectChanged(table, keyColumn, columns, column), _connection, _transaction);
        // Sent untyped, so the source reads the text as the column's own type — the spelling max(column)::text gave.
        command.Parameters.Add(new NpgsqlParameter("since", NpgsqlTypes.NpgsqlDbType.Unknown) { Value = since });
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
    /// Whether the column is <c>smallint</c>, <c>integer</c> or <c>bigint</c>, asked of the catalog once per column per session. An
    /// integer column compared through a cast to text cannot use its index, so the whole table is read for each key; a text one can.
    /// A column the catalog does not show (a table that is not there) is not one, and the read's own error answers for it.
    /// </summary>
    private async ValueTask<bool> IsIntegerColumnAsync(string table, string column, CancellationToken cancellationToken)
    {
        if (_integerColumns.TryGetValue((table, column), out var known))
        {
            return known;
        }

        await using var command = new NpgsqlCommand(
            "SELECT a.atttypid::regtype::text FROM pg_attribute a "
            + "WHERE a.attrelid = to_regclass(@name) AND a.attname = @column AND a.attnum > 0 AND NOT a.attisdropped", _connection, _transaction);
        command.Parameters.AddWithValue("name", QualifiedTable(table));
        command.Parameters.AddWithValue("column", column);
        var type = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        var integer = type is "smallint" or "integer" or "bigint";
        _integerColumns[(table, column)] = integer;
        return integer;
    }

    /// <summary>
    /// Whether <paramref name="text"/> is how an integer column spells a number as text: digits, a minus sign for a negative one, no
    /// leading zero, and within a 64-bit integer. <c>007</c>, <c>+5</c>, <c>-0</c> and <c>7.0</c> are other things that read as numbers —
    /// and are not the text any integer column holds, so they name no row.
    /// </summary>
    internal static bool TryCanonicalInteger(string text, out long value)
    {
        value = 0;
        var digits = text.StartsWith('-') ? text.AsSpan(1) : text.AsSpan();
        if (digits.IsEmpty || (digits.Length > 1 && digits[0] == '0') || (text.Length > 1 && text[0] == '-' && digits[0] == '0'))
        {
            return false;
        }

        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary><c>SELECT key::text, columns… FROM table WHERE key = @key</c> for an integer key column — the number, so the key's index is used.</summary>
    internal static string SelectByIntegerKey(string table, string keyColumn, IReadOnlyList<string> columns)
    {
        var key = Identifier(keyColumn);
        var select = string.Join(", ", columns.Select(Identifier));
        return $"SELECT {key}::text{(columns.Count == 0 ? "" : ", " + select)} FROM {QualifiedTable(table)} WHERE {key} = @key";
    }

    /// <summary><c>SELECT key::text, columns… FROM table WHERE column = @value ORDER BY 1 LIMIT n</c> for an integer field.</summary>
    internal static string SelectWhereInteger(string table, string keyColumn, string column, IReadOnlyList<string> columns, int limit)
    {
        var select = string.Join(", ", columns.Select(Identifier));
        return $"SELECT {Identifier(keyColumn)}::text{(columns.Count == 0 ? "" : ", " + select)} FROM {QualifiedTable(table)} " +
            $"WHERE {Identifier(column)} = @value ORDER BY 1 LIMIT {limit.ToString(CultureInfo.InvariantCulture)}";
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

    /// <summary><c>SELECT max(column)::text FROM table</c> — the latest change the session sees, as the source spells it.</summary>
    internal static string SelectLatest(string table, string column) =>
        $"SELECT max({Identifier(column)})::text FROM {QualifiedTable(table)}";

    /// <summary><c>SELECT key::text, columns… FROM table WHERE column &gt;= @since ORDER BY 1</c> — the rows changed at or after a latest change.</summary>
    internal static string SelectChanged(string table, string keyColumn, IReadOnlyList<string> columns, string column)
    {
        var select = string.Join(", ", columns.Select(Identifier));
        return $"SELECT {Identifier(keyColumn)}::text{(columns.Count == 0 ? "" : ", " + select)} FROM {QualifiedTable(table)} WHERE {Identifier(column)} >= @since ORDER BY 1";
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
