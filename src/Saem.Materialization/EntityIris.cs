namespace Saem.Materialization;

/// <summary>
/// The IRI an entity is named by: the base, the entity type, a slash and the entity's key, each escaped —
/// the same entity type and key give the same IRI on every pass, so a pass made again names the entities
/// it named before. The IRI holds no group: what an entity is does not depend on who may see it, and keeping
/// groups apart is the stores' work, not the names'. An absolute IRI is what Eyu takes as a known entity's
/// key and keeps as that entity's name.
/// </summary>
public sealed class EntityIris
{
    private readonly string _base;

    /// <param name="baseIri">An absolute IRI the entity IRIs are minted under; a trailing slash is added when missing.</param>
    public EntityIris(Uri baseIri)
    {
        ArgumentNullException.ThrowIfNull(baseIri);
        if (!baseIri.IsAbsoluteUri)
        {
            throw new ArgumentException("The base IRI must be absolute.", nameof(baseIri));
        }

        var text = baseIri.AbsoluteUri;
        _base = text.EndsWith('/') ? text : text + "/";
    }

    /// <summary>The IRI of the entity of <paramref name="entityType"/> whose key is <paramref name="key"/>.</summary>
    public string For(string entityType, string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(entityType);
        ArgumentNullException.ThrowIfNull(key);
        return _base + Uri.EscapeDataString(entityType) + "/" + Uri.EscapeDataString(key);
    }

    /// <summary>
    /// The key of the entity of <paramref name="entityType"/> that <paramref name="iri"/> names — the
    /// inverse of <see cref="For"/> — or null when it names no entity of that type under this base.
    /// </summary>
    public string? KeyOf(string entityType, string iri)
    {
        ArgumentException.ThrowIfNullOrEmpty(entityType);
        ArgumentNullException.ThrowIfNull(iri);
        var prefix = _base + Uri.EscapeDataString(entityType) + "/";
        return iri.StartsWith(prefix, StringComparison.Ordinal) && iri.Length > prefix.Length && !iri.AsSpan(prefix.Length).Contains('/')
            ? Uri.UnescapeDataString(iri[prefix.Length..])
            : null;
    }
}
