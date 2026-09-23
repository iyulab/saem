using System.Net;

namespace Saem.Connectors.Formbase;

/// <summary>
/// Formbase could not be read as the connector needs — it refused a request, answered with something
/// the connector cannot carry to Eyu, or does not serve what the connector reads.
/// </summary>
public sealed class FormbaseConnectorException : Exception
{
    public FormbaseConnectorException()
    {
    }

    public FormbaseConnectorException(string message)
        : base(message)
    {
    }

    public FormbaseConnectorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public FormbaseConnectorException(string message, HttpStatusCode statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>The status Formbase answered with, when the failure was a refused request.</summary>
    public HttpStatusCode? StatusCode { get; }
}
