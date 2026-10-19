using Microsoft.Extensions.Options;
using Saem.Connectors.Postgres;
using Saem.Query;

namespace Saem.Mcp;

/// <summary>
/// How a request's person logs in to the source systems. One implementation per way of logging in:
/// today a password per person and source; once a source can take the person's token, an
/// implementation that exchanges it — the tool does not change.
/// </summary>
public interface ISourceLogins
{
    /// <summary>The sessions <paramref name="person"/> reads through, one opener per source system they can log in to.</summary>
    ISourceSessions SessionsFor(Person person);
}

/// <summary>Where the deployment's source systems are, and — for now — how each person logs in to them.</summary>
public sealed class SourceOptions
{
    /// <summary>PostgreSQL sources, by the system name the ontology declares: connection strings without a login.</summary>
    public Dictionary<string, string> Postgres { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The interim password logins, by system and then by person. The person's token cannot yet reach
    /// a PostgreSQL source (the driver does not speak <c>OAUTHBEARER</c>), so each person logs in with
    /// a login of their own that the source maps to their role.
    /// </summary>
    // TODO(upstream: npgsql/npgsql#6156): replace with a token-exchanging ISourceLogins once the driver can present a bearer token.
    public Dictionary<string, Dictionary<string, PasswordLogin>> PasswordLogins { get; } = new(StringComparer.Ordinal);
}

/// <summary>A login to one source: the user name and the password.</summary>
public sealed class PasswordLogin
{
    /// <summary>The source's user name for the person.</summary>
    public string User { get; set; } = "";

    /// <summary>The password.</summary>
    public string Password { get; set; } = "";
}

/// <summary>
/// Logs each person in with their own password login, as configured. A person without a login for a
/// system gets no opener for it, so a lookup that reaches that system fails rather than reading as
/// someone else.
/// </summary>
internal sealed class PasswordSourceLogins : ISourceLogins
{
    private readonly Dictionary<string, PostgresSource> _sources;
    private readonly SourceOptions _options;

    public PasswordSourceLogins(IOptions<SourceOptions> options)
    {
        _options = options.Value;
        _sources = _options.Postgres.ToDictionary(p => p.Key, p => new PostgresSource(p.Value), StringComparer.Ordinal);
    }

    public ISourceSessions SessionsFor(Person person)
    {
        var openers = new Dictionary<string, SourceOpener>(StringComparer.Ordinal);
        foreach (var (system, source) in _sources)
        {
            if (_options.PasswordLogins.TryGetValue(system, out var logins)
                && logins.TryGetValue(person.Subject, out var login))
            {
                var password = login.Password;
                openers.Add(system, source.As(login.User, _ => ValueTask.FromResult(password)));
            }
        }

        return new PersonSessions(openers);
    }
}
