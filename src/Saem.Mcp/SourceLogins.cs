using Microsoft.Extensions.Options;
using Saem.Connectors.MorphDb;
using Saem.Connectors.MySql;
using Saem.Connectors.Postgres;
using Saem.Query;

namespace Saem.Mcp;

/// <summary>
/// How a request's person logs in to the source systems. Each source logs the person in the way it
/// can: a source that takes the person's token gets one exchanged for it; one that cannot yet takes a
/// password login of the person's own. The tool does not change between the two.
/// </summary>
public interface ISourceLogins
{
    /// <summary>
    /// The sessions <paramref name="person"/> reads through for one lookup, one opener per source system
    /// they can log in to. <paramref name="token"/> is the token the request presented, from which a
    /// source's own token is exchanged. Each session carries <paramref name="lookupId"/> where the source
    /// can log it, so the source's record of the login can be matched to the request.
    /// </summary>
    ISourceSessions SessionsFor(Person person, PresentedToken token, string lookupId);
}

/// <summary>Where the deployment's source systems are, and how each person logs in to them.</summary>
public sealed class SourceOptions
{
    /// <summary>PostgreSQL sources, by the system name the ontology declares: connection strings without a login.</summary>
    public Dictionary<string, string> Postgres { get; } = new(StringComparer.Ordinal);

    /// <summary>MySQL sources, by the system name the ontology declares: connection strings without a login.</summary>
    public Dictionary<string, string> MySql { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// MySQL sources that take the person's token as the login, by the system name the ontology declares. The
    /// source's account for a person is named by the token's subject (<see cref="MySqlTokenSourceOptions"/>), so
    /// it needs no per-person configuration either.
    /// </summary>
    public Dictionary<string, MySqlTokenSourceOptions> MySqlToken { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// MorphDB sources, by the system name the ontology declares. Each is read with a token exchanged
    /// for its audience (<see cref="TokenExchange"/>), so it needs no per-person configuration.
    /// </summary>
    public Dictionary<string, MorphDbSourceOptions> MorphDb { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The systems whose tables are a Formbase's projections, by system name: an answer read from such a table, or
    /// refused at it, says when its projection is behind the documents (<see cref="ProjectionSourceOptions"/>).
    /// </summary>
    public Dictionary<string, ProjectionSourceOptions> Projections { get; } = new(StringComparer.Ordinal);

    /// <summary>Where a person's token is exchanged for a source's; required when any source takes a token.</summary>
    public TokenExchangeOptions TokenExchange { get; set; } = new();

    /// <summary>
    /// The interim password logins, by system and then by person. The person's token cannot yet reach
    /// a PostgreSQL source (the driver does not speak <c>OAUTHBEARER</c>), and a MySQL source takes it only
    /// when it is configured under <see cref="MySqlToken"/>; so each person logs in with a login of their own
    /// that the source maps to their role.
    /// </summary>
    // TODO(upstream: npgsql/npgsql#6156): replace with token exchange for PostgreSQL once the driver can present a bearer token.
    public Dictionary<string, Dictionary<string, PasswordLogin>> PasswordLogins { get; } = new(StringComparer.Ordinal);
}

/// <summary>One MorphDB source: the service, the project it reads, and the audience its tokens name.</summary>
public sealed class MorphDbSourceOptions
{
    /// <summary>The service's base address.</summary>
    public string Url { get; set; } = "";

    /// <summary>The project the ontology's tables live in.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>The audience the service accepts (its <c>Security:Tokens:Audience</c>).</summary>
    public string Audience { get; set; } = "";
}

/// <summary>
/// One MySQL source that logs a person in with a token exchanged for it (the server's OpenID Connect login):
/// where it is, and the audience its tokens must carry. The account a session logs in as is named by the
/// token's subject — the source's account for that person is created for that subject.
/// </summary>
public sealed class MySqlTokenSourceOptions
{
    /// <summary>The connection string without a login: server, port, database, TLS.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>The audience the source accepts.</summary>
    public string Audience { get; set; } = "";
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
/// Logs each person in to each configured source the way that source can: MorphDB with a token exchanged
/// from the request's, a MySQL source configured for tokens the same way, PostgreSQL and the other MySQL sources with
/// the person's own password login. A person without a
/// login for a password source gets no opener for it, so a lookup that reaches that system fails rather
/// than reading as someone else.
/// </summary>
internal sealed class ConfiguredSourceLogins : ISourceLogins
{
    private delegate SourceOpener Login(string user, SecretProvider secret, string applicationName);

    private readonly Dictionary<string, Login> _passwordSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (MorphDbSource Source, string Audience)> _tokenSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (MySqlSource Source, string Audience)> _mySqlTokenSources = new(StringComparer.Ordinal);
    private readonly SourceOptions _options;
    private readonly TokenExchange? _exchange;

    public ConfiguredSourceLogins(IOptions<SourceOptions> options, IHttpClientFactory httpClients)
    {
        _options = options.Value;
        foreach (var (system, connectionString) in _options.Postgres)
        {
            Add(system, () => _passwordSources.TryAdd(system, new PostgresSource(connectionString).As));
        }

        foreach (var (system, connectionString) in _options.MySql)
        {
            Add(system, () => _passwordSources.TryAdd(system, new MySqlSource(connectionString).As));
        }

        foreach (var (system, morphDb) in _options.MorphDb)
        {
            if (string.IsNullOrWhiteSpace(morphDb.Audience))
            {
                throw new InvalidOperationException($"The MorphDB source '{system}' names no audience: say which audience its tokens must carry.");
            }

            var http = httpClients.CreateClient("MorphDb:" + system);
            http.BaseAddress = new Uri(morphDb.Url.EndsWith('/') ? morphDb.Url : morphDb.Url + "/");
            Add(system, () => _tokenSources.TryAdd(system, (new MorphDbSource(http, morphDb.ProjectId), morphDb.Audience)));
        }

        foreach (var (system, mySql) in _options.MySqlToken)
        {
            if (string.IsNullOrWhiteSpace(mySql.ConnectionString) || string.IsNullOrWhiteSpace(mySql.Audience))
            {
                throw new InvalidOperationException($"The MySQL source '{system}' takes a token: name its connection string and the audience its tokens must carry.");
            }

            Add(system, () => _mySqlTokenSources.TryAdd(system, (new MySqlSource(mySql.ConnectionString), mySql.Audience)));
        }

        if (_tokenSources.Count > 0 || _mySqlTokenSources.Count > 0)
        {
            _exchange = new TokenExchange(httpClients.CreateClient(nameof(TokenExchange)), _options.TokenExchange);
        }
    }

    private void Add(string system, Func<bool> add)
    {
        if (_passwordSources.ContainsKey(system) || _tokenSources.ContainsKey(system) || _mySqlTokenSources.ContainsKey(system) || !add())
        {
            throw new InvalidOperationException($"The source system '{system}' is configured twice; a system is one source.");
        }
    }

    public ISourceSessions SessionsFor(Person person, PresentedToken token, string lookupId)
    {
        var openers = new Dictionary<string, SourceOpener>(StringComparer.Ordinal);
        foreach (var (system, login) in _passwordSources)
        {
            if (_options.PasswordLogins.TryGetValue(system, out var logins)
                && logins.TryGetValue(person.Subject, out var credential))
            {
                var password = credential.Password;
                openers.Add(system, login(credential.User, _ => ValueTask.FromResult(password), SessionName(lookupId)));
            }
        }

        foreach (var (system, (source, audience)) in _tokenSources)
        {
            openers.Add(system, source.As(ct => _exchange!.ForAsync(token, audience, ct)));
        }

        foreach (var (system, (source, audience)) in _mySqlTokenSources)
        {
            // The account is the person's subject; the token is fetched when the session opens, because it is short-lived.
            openers.Add(system, source.As(person.Subject, ct => _exchange!.ForAsync(token, audience, ct), SessionName(lookupId)));
        }

        return new PersonSessions(openers);
    }

    /// <summary>How a session names itself in the source's log: the server, and the lookup it reads for.</summary>
    internal static string SessionName(string lookupId) => "saem/" + lookupId;
}
