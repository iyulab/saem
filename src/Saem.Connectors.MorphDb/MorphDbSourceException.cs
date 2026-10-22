using System.Net;

namespace Saem.Connectors.MorphDb;

/// <summary>
/// A MorphDB source refused or failed a read — as distinct from a read that found no row the person
/// may see, which is an empty answer, not an error.
/// </summary>
public sealed class MorphDbSourceException : Exception
{
    public MorphDbSourceException()
    {
    }

    public MorphDbSourceException(string message)
        : base(message)
    {
    }

    public MorphDbSourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MorphDbSourceException(string message, HttpStatusCode status)
        : base(message)
    {
        Status = status;
    }

    /// <summary>The status the source answered with, when it answered.</summary>
    public HttpStatusCode? Status { get; }
}
