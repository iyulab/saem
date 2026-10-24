using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Saem.Mcp.Tests;

/// <summary>How the interaction log names a record: a keyed digest, stable under one key and unreadable without it.</summary>
public class InteractionLogTests
{
    private static InteractionLog Log(string? key, string keyId = "k1") =>
        new(Options.Create(new AuditOptions { HmacKey = key, HmacKeyId = keyId }), NullLogger<InteractionLog>.Instance);

    [Fact]
    public void A_record_is_named_the_same_way_under_one_key_and_differently_under_another()
    {
        var name = Log("first-secret").Name("mes", "WorkOrder", "WO-3");

        Assert.Equal(name, Log("first-secret").Name("mes", "WorkOrder", "WO-3"));
        Assert.NotEqual(name, Log("second-secret").Name("mes", "WorkOrder", "WO-3"));
        Assert.Matches("^[0-9a-f]{64}$", name!);
    }

    /// <summary>The parts are length-prefixed, so two different records never run together into one name.</summary>
    [Fact]
    public void Parts_that_would_run_together_name_different_records()
    {
        var log = Log("a-secret");

        Assert.NotEqual(log.Name("mes", "AB", "C"), log.Name("mes", "A", "BC"));
    }

    [Fact]
    public void Without_a_key_no_record_is_named_and_no_key_id_is_logged()
    {
        var log = Log(null);

        Assert.Null(log.Name("mes", "WorkOrder", "WO-3"));
        Assert.Null(log.KeyId);
        Assert.Equal("k2", Log("a-secret", "k2").KeyId);
    }

    private static Interaction Refused(string? actor) => new(
        "lk-1", DateTimeOffset.FromUnixTimeMilliseconds(1_792_900_000_000), 12.5, "alice", actor, "WorkOrder", ["orders_item"], ["name"], "refused",
        [new AccessedRecord("mes", "WorkOrder", "ab12", "read")], new InteractionRefusal("noVisibleResult", 1, "orders_item", "erp"), null, "k1");

    /// <summary>saem SD-16 ④: an interaction as an OCSF 1.9.0 API Activity — the person, the agent for them, the records by digest.</summary>
    [Fact]
    public void An_interaction_is_an_ocsf_api_activity_read_naming_the_person_the_agent_and_the_records_by_digest()
    {
        var ocsf = OcsfEvents.ApiActivityOf(Refused(actor: "agent-for-alice"));

        Assert.Equal((6003, 6, 2, 600302, 1), ((int)ocsf["class_uid"]!, (int)ocsf["category_uid"]!, (int)ocsf["activity_id"]!, (int)ocsf["type_uid"]!, (int)ocsf["severity_id"]!));
        Assert.Equal(1_792_900_000_000, (long)ocsf["time"]!);
        Assert.Equal((1, "refused", "noVisibleResult"), ((int)ocsf["status_id"]!, (string?)ocsf["status"], (string?)ocsf["status_detail"]));
        Assert.Equal("alice", (string?)ocsf["actor"]!["user"]!["uid"]);
        Assert.Equal("agent-for-alice", (string?)ocsf["ai_agent"]!["uid"]);
        Assert.Equal("ai_operation", (string?)ocsf["metadata"]!["profiles"]![0]);
        Assert.Equal(("1.9.0", "lk-1"), ((string?)ocsf["metadata"]!["version"], (string?)ocsf["metadata"]!["uid"]));
        var resource = ocsf["resources"]![0]!;
        Assert.Equal(("WorkOrder", "ab12", "mes"), ((string?)resource["type"], (string?)resource["uid"], (string?)resource["data"]!["system"]));
        Assert.Equal((1, "erp"), ((int)ocsf["unmapped"]!["saem_refusal_at_hop"]!, (string?)ocsf["unmapped"]!["saem_refusal_system"]));
    }

    [Fact]
    public void A_call_with_no_agent_carries_no_ai_profile_and_a_failed_one_is_a_failure()
    {
        var ocsf = OcsfEvents.ApiActivityOf(Refused(actor: null) with { Outcome = "failed", Refusal = null, ErrorType = "source" });

        Assert.Null(ocsf["ai_agent"]);
        Assert.Null(ocsf["metadata"]!["profiles"]);
        Assert.Equal((2, "source"), ((int)ocsf["status_id"]!, (string?)ocsf["status_detail"]));
    }

    [Fact]
    public void With_ocsf_on_each_interaction_is_one_event_line_under_its_own_category()
    {
        var loggers = new Recording();
        var log = new InteractionLog(Options.Create(new AuditOptions { Ocsf = true }), NullLogger<InteractionLog>.Instance, loggers);

        log.Write(Refused(actor: null), span: null);

        var (category, line) = Assert.Single(loggers.Lines);
        Assert.Equal("Saem.Mcp.OcsfEvents", category);
        Assert.Equal(6003, (int)System.Text.Json.Nodes.JsonNode.Parse(line)!["class_uid"]!);
        Assert.Throws<ArgumentNullException>(() => new InteractionLog(Options.Create(new AuditOptions { Ocsf = true }), NullLogger<InteractionLog>.Instance));
    }

    private sealed class Recording : ILoggerFactory, ILogger
    {
        private string _category = "";

        public List<(string Category, string Line)> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName)
        {
            _category = categoryName;
            return this;
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((_category, formatter(state, exception)));
    }
}
