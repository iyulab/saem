using Saem.Connectors.Formbase;
using Saem.Ontology;

namespace Saem.Authoring.Cli;

/// <summary>
/// How a run that did not finish ends the process: with a line on the error stream and a code, whatever was thrown. An exception left
/// unhandled does not end a .NET 10 process on arm64 Linux — it stays up at full CPU after printing — which keeps a container
/// running and its volumes and networks in use, so the command lets nothing escape.
/// </summary>
internal static class RunFailure
{
    private const int Failed = 1;
    private const int Cancelled = 130;

    /// <summary>Writes the failure's line and returns the exit code; <paramref name="cancelled"/> says the person asked to stop.</summary>
    public static int Report(Exception failure, bool cancelled, TextWriter error)
    {
        if (failure is OperationCanceledException && cancelled)
        {
            error.WriteLine("Cancelled.");
            return Cancelled;
        }

        // What the command expects to meet says what happened in its own words; anything else is named by its type, since its
        // message alone rarely says where it came from.
        error.WriteLine(failure is AuthorRunException or OntologyDeclarationException or FormbaseConnectorException or HttpRequestException
            or FormatException or InvalidOperationException or TaskCanceledException { InnerException: TimeoutException }
            ? failure.Message
            : $"{failure.GetType().Name}: {failure.Message}");
        return Failed;
    }
}
