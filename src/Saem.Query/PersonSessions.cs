namespace Saem.Query;

/// <summary>
/// Opens one person's session to a source system: the person's login for that source is already
/// bound, so calling it cannot open a session as anyone else.
/// </summary>
public delegate ValueTask<ISourceSession> SourceOpener(CancellationToken cancellationToken);

/// <summary>
/// The sessions of one lookup, for one person, across the source systems the ontology names: each
/// system's opener carries that person's login for it. Made per lookup and dropped with it, so no
/// login outlives the request it was made for.
/// </summary>
public sealed class PersonSessions : ISourceSessions
{
    private readonly IReadOnlyDictionary<string, SourceOpener> _openers;

    /// <summary>Sessions through <paramref name="openers"/>, by the system name the ontology declares.</summary>
    public PersonSessions(IReadOnlyDictionary<string, SourceOpener> openers)
    {
        ArgumentNullException.ThrowIfNull(openers);
        _openers = openers;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No opener was given for <paramref name="system"/>.</exception>
    public ValueTask<ISourceSession> OpenAsync(string system, CancellationToken cancellationToken = default) =>
        _openers.TryGetValue(system, out var open)
            ? open(cancellationToken)
            : throw new InvalidOperationException(
                $"The ontology names the source system '{system}', and this lookup was given no login for it.");
}
