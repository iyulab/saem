using System.Text.Json;
using Xunit;

namespace Saem.Contracts.Tests;

/// <summary>
/// The answer on the wire: which of the two cases it is travels with it, and what the shape forbids in
/// memory it forbids on the wire too — a payload that would build an answer with no claim, or a claim
/// with no source, is refused while it is read, not passed on.
/// </summary>
public class LookupAnswerJsonTests
{
    private static readonly EntityKey Order = new("Order", "O-1");

    private static readonly EntityKey Supplier = new("Supplier", "S-7");

    private static readonly SourceRecord Erp = new("erp", "suppliers", "S-7");

    private static JsonElement Value(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Write(LookupAnswer answer) => JsonSerializer.Serialize(answer, ContractJson.Default.LookupAnswer);

    private static LookupAnswer? Read(string json) => JsonSerializer.Deserialize(json, ContractJson.Default.LookupAnswer);

    [Fact]
    public void Whose_view_an_answer_is_reads_back_and_is_left_out_when_unknown()
    {
        var refused = new Refused(new Refusal(RefusalReason.NoVisibleResult, 0, null, "erp")) { AnsweredFor = new Viewer("bob", "agent-7") };

        var read = Assert.IsType<Refused>(Read(Write(refused)));

        Assert.Equal(new Viewer("bob", "agent-7"), read.AnsweredFor);
        Assert.Contains("\"answeredFor\":{\"subject\":\"bob\",\"actedBy\":\"agent-7\"}", Write(refused), StringComparison.Ordinal);
        Assert.DoesNotContain("actedBy", Write(refused with { AnsweredFor = new Viewer("bob", null) }), StringComparison.Ordinal);
        Assert.DoesNotContain("answeredFor", Write(refused with { AnsweredFor = null }), StringComparison.Ordinal);
    }

    [Fact]
    public void An_answer_names_its_case_and_reads_back_as_the_same_facts()
    {
        var claim = new Claim(Supplier, "name", Value("\"Acme\""), [Erp], [new Hop("supplied_by", Order, Supplier)]);

        var json = Write(new Answered([claim]));

        using var document = JsonDocument.Parse(json);
        Assert.Equal("answered", document.RootElement.GetProperty("kind").GetString());
        var read = Assert.IsType<Answered>(Read(json));
        var back = Assert.Single(read.Claims);
        Assert.Equal(Supplier, back.Entity);
        Assert.Equal("name", back.Field);
        Assert.Equal("Acme", back.Value.GetString());
        Assert.Equal([Erp], back.Sources);
        Assert.Equal([new Hop("supplied_by", Order, Supplier)], back.Path);
    }

    [Fact]
    public void A_refusal_carries_its_reason_by_name_and_the_statement_that_it_is_not_absence()
    {
        var json = Write(new Refused(new Refusal(RefusalReason.NoVisibleResult, AtHop: 1, Relation: "supplied_by", System: "erp")));

        using var document = JsonDocument.Parse(json);
        var refusal = document.RootElement.GetProperty("refusal");
        Assert.Equal("refused", document.RootElement.GetProperty("kind").GetString());
        Assert.Equal("noVisibleResult", refusal.GetProperty("reason").GetString());
        Assert.Contains("cannot tell", refusal.GetProperty("statement").GetString(), StringComparison.Ordinal);

        var read = Assert.IsType<Refused>(Read(json));
        Assert.Equal(new Refusal(RefusalReason.NoVisibleResult, 1, "supplied_by", "erp"), read.Refusal);
    }

    /// <summary>A hop walked backwards names the partition it was read from; a forward hop carries no such property.</summary>
    [Fact]
    public void A_backward_hop_carries_its_partition_and_a_forward_one_none()
    {
        var back = new Hop("supplies", Supplier, Order) { Partition = "g-plant" };
        var json = Write(new Answered([new Claim(Order, "qty", Value("5"), [Erp], [new Hop("supplied_by", Order, Supplier), back])]));

        using var document = JsonDocument.Parse(json);
        var path = document.RootElement.GetProperty("claims")[0].GetProperty("path");
        Assert.False(path[0].TryGetProperty("partition", out _));
        Assert.Equal("g-plant", path[1].GetProperty("partition").GetString());
        Assert.Equal(back, Assert.IsType<Answered>(Read(json)).Claims[0].Path[1]);
    }

    [Fact]
    public void A_refusal_for_want_of_a_partition_says_so_by_name()
    {
        var json = Write(new Refused(new Refusal(RefusalReason.NoMaterializedPartition, AtHop: 1, Relation: "supplies", System: "mes")));

        using var document = JsonDocument.Parse(json);
        var refusal = document.RootElement.GetProperty("refusal");
        Assert.Equal("noMaterializedPartition", refusal.GetProperty("reason").GetString());
        Assert.Contains("in none that has them", refusal.GetProperty("statement").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_field_travels_as_null_and_stays_a_claim()
    {
        var json = Write(new Answered([new Claim(Supplier, "fax", Value("null"), [Erp], [])]));

        var read = Assert.IsType<Answered>(Read(json));
        Assert.Equal(JsonValueKind.Null, Assert.Single(read.Claims).Value.ValueKind);
    }

    [Fact]
    public void The_case_may_come_after_the_rest_of_the_answer()
    {
        // A consumer that is not .NET writes members in whatever order it likes.
        const string json = """{"refusal":{"reason":"noVisibleResult","atHop":0,"relation":null,"system":"erp"},"kind":"refused"}""";

        Assert.IsType<Refused>(Read(json));
    }

    [Theory]
    [InlineData("""{"kind":"answered","claims":[]}""", "claims")]
    [InlineData("""{"kind":"answered","claims":[{"entity":{"type":"Supplier","key":"S-7"},"field":"name","value":"Acme","sources":[],"path":[]}]}""", "sources")]
    public void A_payload_the_shape_forbids_is_refused_while_it_is_read(string json, string rule)
    {
        // The same constructor rule that refuses the value in memory — not a parser that happened to fail.
        var thrown = Assert.ThrowsAny<ArgumentException>(() => Read(json));

        Assert.Equal(rule, thrown.ParamName);
    }

    [Theory]
    [InlineData("""{"kind":"maybe","claims":[]}""")]
    [InlineData("""{"claims":[]}""")]
    public void A_payload_that_is_neither_case_is_refused(string json)
    {
        var thrown = Record.Exception(() => Read(json));

        Assert.True(thrown is JsonException or NotSupportedException, $"refused as {thrown?.GetType().Name ?? "nothing"}");
    }
}
