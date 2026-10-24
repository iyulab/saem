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
}
