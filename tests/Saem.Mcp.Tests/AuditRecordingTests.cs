using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Saem.Mcp.Tests;

/// <summary>
/// The interaction log is the record of who looked where, so a lookup is not served while nothing can keep that record:
/// an operator's log level that filters the log out must stop the server, not silently drop the audit.
/// </summary>
public sealed class AuditRecordingTests
{
    private const string OntologyJson = """
        [
          { "kind": "entityType", "name": "WorkOrder", "system": "mes", "table": "work_orders", "key": "wo_no", "fields": { "note": "note" } }
        ]
        """;

    private static FakeLogins Alice() => new(new()
    {
        ["alice"] = new() { ["work_orders"] = [FakeLogins.Row("WO-3", ("note", "n"))] },
    });

    private static Dictionary<string, object?> Lookup() => new()
    {
        ["start"] = new Dictionary<string, object?> { ["type"] = "WorkOrder", ["key"] = "WO-3" },
        ["path"] = Array.Empty<string>(),
        ["read"] = new[] { "note" },
    };

    private static Dictionary<string, string?> Identity() => new()
    {
        ["Saem:Identity:Authority"] = TestServer.Issuer,
        ["Saem:Identity:Audience"] = TestServer.Audience,
    };

    [Fact]
    public void An_interaction_log_filtered_below_information_says_it_is_not_recording_and_which_log()
    {
        using var factory = LoggerFactory.Create(b => b.AddProvider(new CapturedLogs()).AddFilter("Saem.Mcp.InteractionLog", LogLevel.Warning));
        var log = new InteractionLog(Options.Create(new AuditOptions()), factory.CreateLogger<InteractionLog>());

        Assert.Contains("Saem.Mcp.InteractionLog", log.NotRecording, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ocsf_log_filtered_below_information_is_named_too_and_an_unfiltered_one_is_recording()
    {
        using var filtered = LoggerFactory.Create(b => b.AddProvider(new CapturedLogs()).AddFilter("Saem.Mcp.OcsfEvents", LogLevel.Error));
        var ocsf = new InteractionLog(Options.Create(new AuditOptions { Ocsf = true }), filtered.CreateLogger<InteractionLog>(), filtered);
        using var open = LoggerFactory.Create(b => b.AddProvider(new CapturedLogs()));
        var recording = new InteractionLog(Options.Create(new AuditOptions { Ocsf = true }), open.CreateLogger<InteractionLog>(), open);

        Assert.Contains("Saem.Mcp.OcsfEvents", ocsf.NotRecording, StringComparison.Ordinal);
        Assert.Null(recording.NotRecording);
    }

    [Fact]
    public async Task A_server_whose_interaction_log_is_filtered_out_does_not_start()
    {
        var started = await Record.ExceptionAsync(async () =>
        {
            await using var server = await TestServer.StartAsync(
                OntologyJson,
                Identity(),
                services =>
                {
                    services.AddSingleton<Saem.Mcp.ISourceLogins>(Alice());
                    services.Configure<LoggerFilterOptions>(o => o.Rules.Add(new LoggerFilterRule(null, "Saem.Mcp.InteractionLog", LogLevel.Warning, null)));
                });
        });

        Assert.NotNull(started);
        Assert.Contains("Saem.Mcp.InteractionLog", started.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lookup_is_refused_before_any_source_is_read_while_the_log_is_switched_off_and_served_again_when_it_is_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var switchable = new Switchable();
        await using var server = await TestServer.StartAsync(
            OntologyJson,
            Identity(),
            services =>
            {
                services.AddSingleton<Saem.Mcp.ISourceLogins>(Alice());
                services.AddSingleton(new InteractionLog(Options.Create(new AuditOptions()), switchable));
                services.PostConfigure<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>(
                    Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme, TestServer.TrustTestKey);
            });
        await using var alice = await server.ClientAsync(TestServer.Token("alice"), ct);

        var served = await alice.CallToolAsync("lookup", Lookup(), cancellationToken: ct);
        switchable.Enabled = false;
        var refused = await alice.CallToolAsync("lookup", Lookup(), cancellationToken: ct);
        switchable.Enabled = true;
        var again = await alice.CallToolAsync("lookup", Lookup(), cancellationToken: ct);

        Assert.NotEqual(true, served.IsError);
        Assert.True(refused.IsError);
        Assert.Contains("interaction log", string.Join(' ', refused.Content.OfType<TextContentBlock>().Select(t => t.Text)), StringComparison.Ordinal);
        Assert.NotEqual(true, again.IsError);
        Assert.Equal(2, switchable.Written);
    }

    private sealed class Switchable : ILogger<InteractionLog>
    {
        public bool Enabled { get; set; } = true;

        public int Written { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => Enabled && logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Written++;
            }
        }
    }
}
