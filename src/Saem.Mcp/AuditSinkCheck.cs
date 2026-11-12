using Microsoft.Extensions.Hosting;

namespace Saem.Mcp;

/// <summary>
/// At start, refuses to run a server whose interaction log would write nothing — a log level above Information on its category
/// drops every interaction silently, and a deployment that thinks it is audited is worse off than one that failed to start. The
/// check is repeated on every call (<see cref="LookupTool"/>), for a level raised while the server runs.
/// </summary>
internal sealed class AuditSinkCheck(InteractionLog interactions) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        interactions.NotRecording is { } problem
            ? throw new InvalidOperationException(problem + " Lower it (Logging:LogLevel) so each lookup leaves its record; a lookup nobody can account for is not served.")
            : Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
