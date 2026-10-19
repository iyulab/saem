using Npgsql;
using Saem.Query;

namespace Saem.Connectors.Postgres;

/// <summary>
/// One PostgreSQL source system: where it is, and nothing about who reads it. A session is opened
/// for one person with <see cref="As"/>.
/// </summary>
public sealed class PostgresSource
{
    private readonly string _connectionString;

    /// <summary>
    /// A source at <paramref name="connectionString"/> — host, port, database, TLS. A user name or
    /// password in it is refused: the login is the person's, given per lookup.
    /// </summary>
    /// <exception cref="ArgumentException">The connection string names a login.</exception>
    public PostgresSource(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.IsNullOrEmpty(builder.Username) || !string.IsNullOrEmpty(builder.Password))
        {
            throw new ArgumentException(
                "A source's connection string says where it is, not who logs in: leave out the user name and the password.",
                nameof(connectionString));
        }

        // A connection is the person's: none is pooled between lookups, so none can be handed to the
        // next person, and none outlives the lookup that opened it.
        builder.Pooling = false;
        _connectionString = builder.ConnectionString;
    }

    /// <summary>
    /// Opens sessions as <paramref name="user"/>, logging in with what <paramref name="secret"/>
    /// returns when a connection is opened. The source decides what that login may read.
    /// <paramref name="applicationName"/>, when given, names the session in the source — its
    /// <c>application_name</c>, which the source's own connection log records beside the login — so the
    /// source's log can be matched to the request the session was opened for.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="applicationName"/> is longer than PostgreSQL keeps (63 bytes) or not plain ASCII.</exception>
    public SourceOpener As(string user, SecretProvider secret, string? applicationName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        ArgumentNullException.ThrowIfNull(secret);
        if (applicationName is not null && (applicationName.Length > 63 || !applicationName.All(c => c is >= ' ' and <= '~')))
        {
            // PostgreSQL truncates a longer name and replaces non-ASCII, so the log would not show what was asked.
            throw new ArgumentException("An application name is at most 63 printable ASCII characters.", nameof(applicationName));
        }

        return async cancellationToken =>
        {
            var builder = new NpgsqlDataSourceBuilder(_connectionString);
            builder.ConnectionStringBuilder.Username = user;
            if (applicationName is not null)
            {
                builder.ConnectionStringBuilder.ApplicationName = applicationName;
            }

            // Npgsql asks for both; a session is only ever opened asynchronously, so the synchronous one
            // is never called — and says so rather than blocking on the secret if it ever were.
            builder.UsePasswordProvider(
                passwordProvider: _ => throw new InvalidOperationException("A source session is opened asynchronously only."),
                passwordProviderAsync: (_, ct) => secret(ct));
            var dataSource = builder.Build();
            try
            {
                return await PostgresSourceSession.OpenAsync(dataSource, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await dataSource.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        };
    }
}
