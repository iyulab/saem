using Microsoft.Extensions.Logging;

namespace Saem.Console.Tests;

/// <summary>
/// A logger provider that keeps nothing but says it is listening. A host that hosts the MCP server needs one: the server refuses to run
/// where its interaction log would be written nowhere, and a host whose providers were cleared writes it nowhere.
/// </summary>
internal sealed class DiscardingLogs : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Listening();

    public void Dispose()
    {
    }

    private sealed class Listening : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }
}
