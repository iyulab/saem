using Npgsql;
using Saem.Query;

namespace Saem.Connectors.Postgres;

/// <summary>
/// The secret a person logs in to a source with, asked for each time a connection to the source is
/// opened — so it is fetched when needed and never kept here.
/// </summary>
public delegate ValueTask<string> SecretProvider(CancellationToken cancellationToken);

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
    /// </summary>
    public SourceOpener As(string user, SecretProvider secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        ArgumentNullException.ThrowIfNull(secret);
        return async cancellationToken =>
        {
            var builder = new NpgsqlDataSourceBuilder(_connectionString);
            builder.ConnectionStringBuilder.Username = user;
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
