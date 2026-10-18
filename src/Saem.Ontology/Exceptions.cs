namespace Saem.Ontology;

/// <summary>The declarations do not form a consistent ontology. Every problem found is listed.</summary>
public sealed class OntologyDeclarationException : Exception
{
    /// <summary>Creates the exception from the problems found.</summary>
    public OntologyDeclarationException(IReadOnlyList<string> problems)
        : base("The ontology declarations are not consistent: " + string.Join(" ", problems))
    {
        Problems = problems;
    }

    /// <summary>Creates the exception with a single message.</summary>
    public OntologyDeclarationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Problems = [message];
    }

    /// <summary>The problems found, one sentence each.</summary>
    public IReadOnlyList<string> Problems { get; }
}

/// <summary>
/// A lookup names a type, relation or field the ontology does not declare, or follows a relation from
/// a type it does not leave. The caller's question is malformed — this is not a refusal, which is an
/// answer about the sources.
/// </summary>
public sealed class UnknownTermException : Exception
{
    /// <summary>Creates the exception from the problems found.</summary>
    public UnknownTermException(IReadOnlyList<string> problems)
        : base("The lookup is not in the ontology's terms: " + string.Join(" ", problems))
    {
        Problems = problems;
    }

    /// <summary>The problems found, one sentence each.</summary>
    public IReadOnlyList<string> Problems { get; }
}
