using MySqlConnector;
using Saem.Query;

namespace Saem.Connectors.MySql;

/// <summary>
/// One MySQL source system: where it is, and nothing about who reads it. A session is opened for one
/// person with <see cref="As"/>.
/// </summary>
public sealed class MySqlSource
{
    private readonly string _connectionString;

    /// <summary>
    /// A source at <paramref name="connectionString"/> — server, port, database, TLS. A user name or
    /// password in it is refused: the login is the person's, given per lookup.
    /// </summary>
    /// <exception cref="ArgumentException">The connection string names a login.</exception>
    public MySqlSource(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new MySqlConnectionStringBuilder(connectionString);
        if (!string.IsNullOrEmpty(builder.UserID) || !string.IsNullOrEmpty(builder.Password))
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
    /// returns when the session opens. The source decides what that login may read.
    /// <paramref name="applicationName"/>, when given, names the session in the source — the
    /// <c>program_name</c> connection attribute, which the source records with the session — so the
    /// source's record can be matched to the request the session was opened for.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="applicationName"/> is longer than 63 characters or not plain ASCII.</exception>
    public SourceOpener As(string user, SecretProvider secret, string? applicationName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        ArgumentNullException.ThrowIfNull(secret);
        if (applicationName is not null && (applicationName.Length > 63 || !applicationName.All(c => c is >= ' ' and <= '~')))
        {
            // The same bound as the PostgreSQL adapter, so one lookup id names a session the same way in every source.
            throw new ArgumentException("An application name is at most 63 printable ASCII characters.", nameof(applicationName));
        }

        return async cancellationToken =>
        {
            var builder = new MySqlConnectionStringBuilder(_connectionString)
            {
                UserID = user,
                Password = await secret(cancellationToken).ConfigureAwait(false),
            };
            if (applicationName is not null)
            {
                builder.ApplicationName = applicationName;
            }

            return await MySqlSourceSession.OpenAsync(builder.ConnectionString, cancellationToken).ConfigureAwait(false);
        };
    }
}
