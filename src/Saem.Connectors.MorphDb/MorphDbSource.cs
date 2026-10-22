using Saem.Query;

namespace Saem.Connectors.MorphDb;

/// <summary>
/// One MorphDB source system: where it is and which project it reads, and nothing about who reads it.
/// A session is opened for one person with <see cref="As"/>.
/// </summary>
public sealed class MorphDbSource
{
    private readonly HttpClient _http;
    private readonly Guid _projectId;

    /// <summary>
    /// A source at <paramref name="http"/>'s base address, reading <paramref name="projectId"/>. The
    /// caller owns the client and shares it across lookups; a client that already carries an
    /// <c>Authorization</c> header is refused — the credential is the person's, given per lookup.
    /// </summary>
    /// <exception cref="ArgumentException">The client has no base address, or carries a credential.</exception>
    public MorphDbSource(HttpClient http, Guid projectId)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.BaseAddress is null)
        {
            throw new ArgumentException("A source's client says where the service is: set its base address.", nameof(http));
        }

        if (http.DefaultRequestHeaders.Authorization is not null)
        {
            throw new ArgumentException(
                "A source's client says where it is, not who reads: leave out the Authorization header.",
                nameof(http));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("A MorphDB source reads one project: give its id.", nameof(projectId));
        }

        _http = http;
        _projectId = projectId;
    }

    /// <summary>
    /// Opens sessions as the person whose access token <paramref name="token"/> returns — a token for
    /// this source's audience, obtained for that person (an exchange, not the token saem itself was
    /// called with). It is asked for once per session, when the session opens.
    /// </summary>
    public SourceOpener As(SecretProvider token)
    {
        ArgumentNullException.ThrowIfNull(token);

        return async cancellationToken =>
        {
            var presented = await token(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(presented))
            {
                throw new MorphDbSourceException("The token provider returned no token; the session was not opened.");
            }

            return new MorphDbSourceSession(_http, _projectId, presented);
        };
    }
}
