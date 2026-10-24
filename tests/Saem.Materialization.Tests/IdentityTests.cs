using Saem.Contracts;
using Saem.Ontology;
using Xunit;
using static Saem.Materialization.Tests.EdgeMaterializerTests;

namespace Saem.Materialization.Tests;

/// <summary>
/// saem D-81: what a field-matched identity finds — entities whose matched fields hold the same values, read as the
/// group — and how a canonical id carries on from pass to pass: issued once, kept by the cluster that keeps most of
/// its members, the older kept on a merge, a new one for the part split off.
/// </summary>
public sealed class IdentityTests
{
    private const string Base = "https://example.org/ontology/";

    private static readonly EntityIris Iris = new(new Uri(Base));

    private static readonly EntityTypeDeclaration Material = new(
        "Material", "mes", "materials", "mat_no", new Dictionary<string, string> { ["erpCode"] = "erp_item_code", ["plant"] = "plant" });

    private static readonly EntityTypeDeclaration Item = new(
        "Item", "erp", "items", "item_code", new Dictionary<string, string> { ["code"] = "item_code", ["plant"] = "plant_no" })
    {
        Kinds = new Dictionary<string, FieldKind> { ["plant"] = FieldKind.Number },
    };

    private static Ontology.Ontology With(params Declaration[] identities) => Ontology.Ontology.Create([Material, Item, .. identities]);

    private static readonly IdentityDeclaration SameItem = new("same_item", "Material", "Item", IdentityDecision.Automatic)
    {
        Match = [new FieldPair("erpCode", "code")],
    };

    private static string M(string key) => Base + "Material/" + key;

    private static string I(string key) => Base + "Item/" + key;

    [Fact]
    public async Task Entities_of_two_types_whose_matched_fields_hold_the_same_values_are_one_cluster()
    {
        var sessions = new Sessions(new()
        {
            ["mes/materials"] =
            [
                Row("M-1", ("erp_item_code", "I-100")),
                Row("M-2", ("erp_item_code", " I-100 ")),
                Row("M-3", ("erp_item_code", "I-200")),
                Row("M-4", ("erp_item_code", null)),
                Row("M-5", ("erp_item_code", "I-999")),
            ],
            ["erp/items"] = [Row("I-100", ("item_code", "I-100")), Row("I-200", ("item_code", "I-200")), Row("I-300", ("item_code", "I-300"))],
        });

        var clusters = await new IdentityMatcher(With(SameItem), Iris).MatchAsync(sessions, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        // M-4 holds no value and M-5's names no item: neither is one with anything. Only the matched columns are read.
        Assert.Equal(
            [new IdentityCluster("same_item", [I("I-100"), M("M-1"), M("M-2")]), new IdentityCluster("same_item", [I("I-200"), M("M-3")])],
            clusters);
        Assert.Equal(["mes materials mat_no [erp_item_code]", "erp items item_code [item_code]"], sessions.Scans);
        Assert.Equal(2, sessions.Closed);
    }

    [Fact]
    public async Task Every_matched_field_must_hold_the_same_value_and_a_number_field_compares_as_the_integer()
    {
        var sessions = new Sessions(new()
        {
            ["mes/materials"] = [Row("M-1", ("erp_item_code", "I-100"), ("plant", "7")), Row("M-2", ("erp_item_code", "I-100"), ("plant", "8"))],
            ["erp/items"] = [Row("I-100", ("item_code", "I-100"), ("plant_no", "0007"))],
        });
        var byPlant = SameItem with { Match = [new FieldPair("erpCode", "code"), new FieldPair("plant", "plant")] };

        var cluster = Assert.Single(await new IdentityMatcher(With(byPlant), Iris).MatchAsync(sessions, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));

        Assert.Equal([I("I-100"), M("M-1")], cluster.Members);
    }

    [Fact]
    public async Task Within_one_type_two_entities_with_the_same_values_are_duplicates_and_one_alone_is_nothing()
    {
        var duplicates = new IdentityDeclaration("duplicate_material", "Material", "Material", IdentityDecision.Confirmed)
        {
            Match = [new FieldPair("erpCode", "erpCode")],
        };
        var sessions = new Sessions(new()
        {
            ["mes/materials"] = [Row("M-1", ("erp_item_code", "I-100")), Row("M-2", ("erp_item_code", "I-100")), Row("M-3", ("erp_item_code", "I-200"))],
        });

        var clusters = await new IdentityMatcher(With(duplicates), Iris).MatchAsync(sessions, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([new IdentityCluster("duplicate_material", [M("M-1"), M("M-2")])], clusters);
        Assert.Single(sessions.Scans);
    }

    [Fact]
    public async Task An_identity_by_a_linkage_rule_is_not_matched_by_fields()
    {
        var linked = new IdentityDeclaration("linked", "Material", "Item", IdentityDecision.Confirmed) { Linkage = "names" };
        var sessions = new Sessions(new());

        Assert.Empty(await new IdentityMatcher(With(linked), Iris).MatchAsync(sessions, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(sessions.Scans);
    }

    private static readonly DateTimeOffset Then = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Now = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    private static IdentityCluster Cluster(params string[] members) => new("same_item", [.. members.Order(StringComparer.Ordinal)]);

    private static CanonicalIdentity Held(string id, DateTimeOffset issued, params string[] members) => new("same_item", id, issued, [.. members.Order(StringComparer.Ordinal)]);

    private static IdentitySuccessionResult Succeed(IReadOnlyList<CanonicalIdentity> held, params IdentityCluster[] clusters)
    {
        var issued = 0;
        return IdentitySuccession.Succeed(held, clusters, Now, () => $"new-{++issued}");
    }

    [Fact]
    public void A_thing_found_for_the_first_time_is_issued_an_id_and_keeps_it_while_its_members_change()
    {
        var first = Succeed([], Cluster("a", "b"));
        Assert.Equal([Held("new-1", Now, "a", "b")], first.Standing);

        var again = Succeed(first.Standing, Cluster("a", "b", "c"));
        Assert.Equal([Held("new-1", Now, "a", "b", "c")], again.Standing);
        Assert.Empty(again.Retired);
    }

    [Fact]
    public void A_merge_keeps_the_older_id_and_retires_the_other()
    {
        var result = Succeed([Held("young", Now, "a", "b"), Held("old", Then, "c", "d")], Cluster("a", "b", "c", "d"));

        Assert.Equal([Held("old", Then, "a", "b", "c", "d")], result.Standing);
        Assert.Equal(["young"], result.Retired);
    }

    [Fact]
    public void A_split_leaves_the_id_with_the_part_that_kept_most_members_and_issues_the_other_a_new_one()
    {
        var result = Succeed([Held("x", Then, "a", "b", "c", "d", "e")], Cluster("a", "b"), Cluster("c", "d", "e"), Cluster("f", "g"));

        Assert.Equal([Held("new-1", Now, "a", "b"), Held("x", Then, "c", "d", "e"), Held("new-2", Now, "f", "g")], result.Standing);
        Assert.Empty(result.Retired);

        var tie = Succeed([Held("x", Then, "a", "b", "c", "d")], Cluster("c", "d"), Cluster("a", "b"));
        Assert.Equal([Held("x", Then, "a", "b"), Held("new-1", Now, "c", "d")], tie.Standing);
    }

    [Fact]
    public void An_id_whose_members_are_in_no_cluster_any_more_is_retired()
    {
        var result = Succeed([Held("gone", Then, "a", "b")], Cluster("c", "d"));

        Assert.Equal([Held("new-1", Now, "c", "d")], result.Standing);
        Assert.Equal(["gone"], result.Retired);
    }

    [Fact]
    public void Clusters_that_share_an_entity_or_mix_identities_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Succeed([], Cluster("a", "b"), Cluster("b", "c")));
        Assert.Throws<ArgumentException>(() => Succeed([], Cluster("a", "b"), new IdentityCluster("other", ["c", "d"])));
    }

    private static PersonJudgment Judged(string a, string b, bool same, int day = 1) =>
        new("same_item", a, b, same, "p-1", Then.AddDays(day));

    private static IReadOnlyList<IdentityCluster> Apply(IdentityDecision decision, IdentityCluster[] clusters, params PersonJudgment[] judgments) =>
        IdentityJudgments.Apply(SameItem with { Decision = decision }, clusters, judgments);

    [Fact]
    public void A_pair_a_person_parted_leaves_the_rules_cluster_and_the_rest_stands()
    {
        Assert.Equal(
            [Cluster("c", "d")],
            Apply(IdentityDecision.Automatic, [Cluster("a", "b", "c", "d")], Judged("b", "a", same: false)));
    }

    [Fact]
    public void A_pair_a_person_joined_is_one_thing_and_joins_the_things_either_is_in()
    {
        Assert.Equal(
            [Cluster("a", "b", "c", "d"), Cluster("x", "y")],
            Apply(IdentityDecision.Automatic, [Cluster("a", "b"), Cluster("c", "d")], Judged("d", "a", same: true), Judged("y", "x", same: true)));

        // A person may also put back one of a parted pair.
        Assert.Equal(
            [Cluster("a", "c", "d")],
            Apply(IdentityDecision.Automatic, [Cluster("a", "b", "c", "d")], Judged("a", "b", same: false), Judged("a", "c", same: true)));
    }

    [Fact]
    public void For_a_person_settled_identity_only_what_people_joined_is_one_thing()
    {
        Assert.Empty(Apply(IdentityDecision.Confirmed, [Cluster("a", "b")]));
        Assert.Equal([Cluster("a", "b")], Apply(IdentityDecision.Confirmed, [Cluster("a", "b", "c")], Judged("a", "b", same: true)));
    }

    [Fact]
    public void A_pairs_latest_judgment_is_the_one_that_counts()
    {
        Assert.Equal(
            [Cluster("a", "b")],
            Apply(IdentityDecision.Confirmed, [], Judged("a", "b", same: false, day: 1), Judged("b", "a", same: true, day: 2)));
        Assert.Empty(Apply(IdentityDecision.Confirmed, [], Judged("a", "b", same: true, day: 2), Judged("a", "b", same: false, day: 3)));
    }
}
