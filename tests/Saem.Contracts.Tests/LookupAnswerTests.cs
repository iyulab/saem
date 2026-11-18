using System.Text.Json;
using Xunit;

namespace Saem.Contracts.Tests;

/// <summary>
/// The answer's shape is where "no claim without a source" and "no answer that is neither facts nor a
/// refusal" are held: a value that breaks either cannot be built, so no code path can return one.
/// </summary>
public class LookupAnswerTests
{
    private static readonly EntityKey Supplier = new("Supplier", "S-7");

    private static readonly SourceRecord Erp = new("erp", "suppliers", "S-7");

    private static JsonElement Value(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("S-7", true)]
    [InlineData(null, false)]
    public void A_start_names_its_entity_by_a_key_or_by_a_field_and_never_both_nor_neither(string? key, bool where)
    {
        var thrown = Assert.Throws<ArgumentException>(() => new LookupStart("Supplier", key, where ? new FieldMatch("name", "Acme") : null));

        Assert.Equal("key", thrown.ParamName);
    }

    [Fact]
    public void Candidates_are_two_or_more()
    {
        var one = new Candidate(Supplier, Erp);

        Assert.Equal("entities", Assert.Throws<ArgumentException>(() => new Candidates([one])).ParamName);
        Assert.Equal(2, new Candidates([one, one with { Entity = new EntityKey("Supplier", "S-8") }]).Entities.Count);
    }

    [Fact]
    public void A_claim_without_a_source_cannot_be_built()
    {
        var thrown = Assert.Throws<ArgumentException>(() => new Claim(Supplier, "name", Value("\"Acme\""), [], [[]]));

        Assert.Equal("sources", thrown.ParamName);
    }

    [Fact]
    public void An_answer_with_no_claims_cannot_be_built()
    {
        var thrown = Assert.Throws<ArgumentException>(() => new Answered([]));

        Assert.Equal("claims", thrown.ParamName);
    }

    [Fact]
    public void A_claim_carries_its_sources_and_the_path_that_reached_it()
    {
        var order = new EntityKey("Order", "O-1");
        var hop = new Hop("supplied_by", order, Supplier);

        var claim = new Claim(Supplier, "name", Value("\"Acme\""), [Erp], [[hop]]);

        Assert.Equal([Erp], claim.Sources);
        Assert.Equal([hop], claim.Paths[0]);
        Assert.Equal("Acme", claim.Value.GetString());
    }

    [Fact]
    public void A_claim_without_a_way_it_was_reached_cannot_be_built()
    {
        var thrown = Assert.Throws<ArgumentException>(() => new Claim(Supplier, "name", Value("\"Acme\""), [Erp], []));

        Assert.Equal("paths", thrown.ParamName);
    }

    [Fact]
    public void A_fact_reached_two_ways_is_one_claim_with_both_paths()
    {
        var first = new Hop("supplied_by", new EntityKey("Order", "O-1"), Supplier);
        var second = new Hop("also_supplied_by", new EntityKey("Order", "O-2"), Supplier);

        var claim = new Claim(Supplier, "name", Value("\"Acme\""), [Erp], [[first], [second]]);

        Assert.Equal([[first], [second]], claim.Paths);
    }

    [Fact]
    public void A_fact_stated_twice_in_one_answer_cannot_be_built()
    {
        var one = new Claim(Supplier, "name", Value("\"Acme\""), [Erp], [[new Hop("supplied_by", new EntityKey("Order", "O-1"), Supplier)]]);
        var again = new Claim(Supplier, "name", Value("\"Acme\""), [Erp], [[new Hop("supplied_by", new EntityKey("Order", "O-2"), Supplier)]]);

        var thrown = Assert.Throws<ArgumentException>(() => new Answered([one, again]));

        Assert.Equal("claims", thrown.ParamName);
    }

    [Fact]
    public void The_same_field_with_another_value_or_another_entity_is_another_fact()
    {
        var acme = new Claim(Supplier, "name", Value("\"Acme\""), [Erp], [[]]);
        var other = new Claim(Supplier, "name", Value("\"Acme Ltd\""), [Erp], [[]]);
        var elsewhere = new Claim(new EntityKey("Supplier", "S-8"), "name", Value("\"Acme\""), [Erp], [[]]);

        Assert.Equal(3, new Answered([acme, other, elsewhere]).Claims.Count);
    }

    [Fact]
    public void An_empty_field_is_a_claim_with_a_null_value_not_a_refusal()
    {
        var claim = new Claim(Supplier, "fax", Value("null"), [Erp], [[]]);

        Assert.Equal(JsonValueKind.Null, claim.Value.ValueKind);
    }

    [Fact]
    public void A_refusal_says_where_the_walk_stopped_and_never_that_nothing_exists()
    {
        var refusal = new Refusal(RefusalReason.NoVisibleResult, AtHop: 1, Relation: "supplied_by", System: "erp");

        Assert.Contains("cannot tell", refusal.Statement, StringComparison.Ordinal);
        Assert.DoesNotContain("does not exist", refusal.Statement, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not found", refusal.Statement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_refusal_reason_has_a_statement()
    {
        foreach (var reason in Enum.GetValues<RefusalReason>())
        {
            Assert.False(string.IsNullOrWhiteSpace(new Refusal(reason, 0, null, "erp").Statement));
        }
    }

    [Fact]
    public void An_answer_is_either_facts_or_a_refusal()
    {
        LookupAnswer[] answers =
        [
            new Answered([new Claim(Supplier, "name", Value("\"Acme\""), [Erp], [[]])]),
            new Refused(new Refusal(RefusalReason.NoVisibleResult, 0, null, "erp")),
        ];

        Assert.All(answers, answer => Assert.True(answer is Answered or Refused));
    }
}
