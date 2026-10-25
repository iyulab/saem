using System.Text.Json.Nodes;
using Saem.Contracts;
using Saem.Ontology;
using Xunit;

namespace Saem.Materialization.Tests;

/// <summary>
/// saem D-81 ① ③: what a group's identities found, kept in its own Formbase under three form types of text fields —
/// canonical ids, cross-references and grounds, none holding a record's value — with the ids carried on from pass to
/// pass, and a person-settled identity filing grounds only.
/// </summary>
public sealed class FormbaseIdentityStoreTests
{
    private static readonly string[] FormTypes = ["identities", "identity_members", "identity_grounds"];

    private static readonly string[] JudgedFields = ["identity", "first_iri", "second_iri", "rule", "score", "judged_by"];

    private static readonly DateTimeOffset First = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Second = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    private static Ontology.Ontology Plant(IdentityDecision decision = IdentityDecision.Automatic) => Ontology.Ontology.Create(
    [
        new EntityTypeDeclaration("Material", "mes", "materials", "mat_no", new Dictionary<string, string> { ["erpCode"] = "erp_item_code" }),
        new EntityTypeDeclaration("Item", "erp", "items", "item_code", new Dictionary<string, string> { ["code"] = "item_code" }),
        new IdentityDeclaration("same_item", "Material", "Item", decision) { Match = [new FieldPair("erpCode", "code")] },
    ]);

    private static IdentityCluster Cluster(params string[] members) => new("same_item", [.. members.Order(StringComparer.Ordinal)]);

    private static FormbaseIdentityStore Store(FakeFormbase formbase, DateTimeOffset at)
    {
        var issued = 0;
        return new FormbaseIdentityStore(formbase.Client, time: new FixedClock(at), issue: () => $"id-{at:MMdd}-{++issued}");
    }

    private static async IAsyncEnumerable<IdentityCluster> Of(params IdentityCluster[] clusters)
    {
        foreach (var cluster in clusters)
        {
            yield return cluster;
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_first_pass_declares_three_text_shapes_and_files_an_id_its_members_and_their_grounds()
    {
        var formbase = new FakeFormbase();

        var write = await Store(formbase, First).ReplaceAsync(Plant(), Of(Cluster("i/1", "m/1", "m/2")), TestContext.Current.CancellationToken);

        Assert.Equal(new IdentityWrite(new(1, 0, 0), new(3, 0, 0), new(2, 0, 0)) { Issued = 1 }, write);
        Assert.Equal(
            [("saem_identities", "identity,canonical_id,issued"), ("saem_identity_members", "identity,entity_iri,canonical_id"),
             ("saem_identity_grounds", "identity,first_iri,second_iri,rule,score,judged_by,generation")],
            FormTypes.Select(t => Shape(formbase.Type(t).Declaration!)));
        Assert.Equal(
            [new CanonicalIdentity("same_item", "id-1001-1", First, ["i/1", "m/1", "m/2"])],
            await Store(formbase, Second).ReadAsync(TestContext.Current.CancellationToken));

        // A ground pairs each member with the first, by IRI, under the rule that judged it — no value of any record.
        Assert.Equal(
            ["same_item i/1 m/1 match 1 rule", "same_item i/1 m/2 match 1 rule"],
            formbase.Type("identity_grounds").Rows.Select(r => string.Join(' ', JudgedFields.Select(f => (string?)r.Body![f]))));
    }

    [Fact]
    public async Task A_pass_made_again_sends_nothing_and_keeps_the_id_and_the_pass_that_first_judged_each_ground()
    {
        var formbase = new FakeFormbase();
        await Store(formbase, First).ReplaceAsync(Plant(), Of(Cluster("i/1", "m/1")), TestContext.Current.CancellationToken);
        var sent = formbase.Type("identity_grounds").Stream.Count + formbase.Type("identities").Stream.Count + formbase.Type("identity_members").Stream.Count;

        var write = await Store(formbase, Second).ReplaceAsync(Plant(), Of(Cluster("i/1", "m/1")), TestContext.Current.CancellationToken);

        Assert.Equal(new IdentityWrite(new(0, 1, 0), new(0, 2, 0), new(0, 1, 0)), write);
        Assert.Equal(sent, formbase.Type("identity_grounds").Stream.Count + formbase.Type("identities").Stream.Count + formbase.Type("identity_members").Stream.Count);
        Assert.Equal(First.ToString("O"), (string?)Assert.Single(formbase.Type("identity_grounds").Rows).Body!["generation"]);
    }

    [Fact]
    public async Task A_merge_keeps_the_older_id_corrects_the_moved_members_and_retires_the_younger_one()
    {
        var formbase = new FakeFormbase();
        await Store(formbase, First).ReplaceAsync(Plant(), Of(Cluster("i/1", "m/1")), TestContext.Current.CancellationToken);
        await Store(formbase, Second).ReplaceAsync(Plant(), Of(Cluster("i/1", "m/1"), Cluster("i/2", "m/2")), TestContext.Current.CancellationToken);

        // The two things turn out to be one: the older id survives.
        var write = await Store(formbase, Second.AddDays(1)).ReplaceAsync(Plant(), Of(Cluster("i/1", "i/2", "m/1", "m/2")), TestContext.Current.CancellationToken);

        Assert.Equal(1, write.Merged);
        Assert.Equal(new RecordWrite(0, 1, 1), write.Canonical);
        Assert.Equal(new RecordWrite(2, 2, 0), write.Members);
        Assert.Equal(
            [new CanonicalIdentity("same_item", "id-1001-1", First, ["i/1", "i/2", "m/1", "m/2"])],
            await Store(formbase, Second).ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_scored_cluster_files_the_pairs_it_was_scored_on_under_its_rule_with_their_scores()
    {
        var formbase = new FakeFormbase();
        var scored = Cluster("i/1", "m/1", "m/2") with
        {
            Grounds = [new IdentityGround("i/1", "m/1", "names", 9.125), new IdentityGround("m/1", "m/2", "names", 4.5)],
        };

        await Store(formbase, First).ReplaceAsync(Plant(), Of(scored), TestContext.Current.CancellationToken);

        // The pairs are the ones the rule scored — not each member with the first — and the score reads back the same.
        Assert.Equal(
            ["same_item i/1 m/1 names 9.125 rule", "same_item m/1 m/2 names 4.5 rule"],
            formbase.Type("identity_grounds").Rows.Select(r => string.Join(' ', JudgedFields.Select(f => (string?)r.Body![f]))));
        Assert.True((await Store(formbase, Second).CompareAsync(Plant(), Of(scored), TestContext.Current.CancellationToken)).Agrees);
    }

    [Fact]
    public async Task A_person_settled_identity_files_its_pairs_as_grounds_and_issues_no_id()
    {
        var formbase = new FakeFormbase();

        var write = await Store(formbase, First).ReplaceAsync(Plant(IdentityDecision.Confirmed), Of(Cluster("i/1", "m/1")), TestContext.Current.CancellationToken);

        Assert.Equal(new IdentityWrite(new(0, 0, 0), new(0, 0, 0), new(1, 0, 0)), write);
        Assert.Empty(await Store(formbase, Second).ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_persons_judgment_is_kept_by_every_pass_and_wins_over_the_rule()
    {
        var formbase = new FakeFormbase();
        var confirmed = Plant(IdentityDecision.Confirmed);
        await Store(formbase, First).ReplaceAsync(confirmed, Of(Cluster("i/1", "m/1"), Cluster("i/2", "m/2")), TestContext.Current.CancellationToken);

        await Store(formbase, First).JudgeAsync(confirmed, new PersonJudgment("same_item", "m/1", "i/1", Same: true, "p-7", First.AddHours(1)), TestContext.Current.CancellationToken);
        var write = await Store(formbase, Second).ReplaceAsync(confirmed, Of(Cluster("i/1", "m/1"), Cluster("i/2", "m/2")), TestContext.Current.CancellationToken);

        // Only the pair a person confirmed is one thing; the other stays a candidate.
        Assert.Equal(1, write.Issued);
        Assert.Equal([new CanonicalIdentity("same_item", "id-1025-1", Second, ["i/1", "m/1"])], await Store(formbase, Second).ReadAsync(TestContext.Current.CancellationToken));
        Assert.Contains(
            formbase.Type("identity_grounds").Rows,
            r => (string?)r.Body!["rule"] == "person" && (string?)r.Body["judged_by"] == "p-7" && (string?)r.Body["first_iri"] == "i/1");
        Assert.True((await Store(formbase, Second).CompareAsync(confirmed, Of(Cluster("i/1", "m/1"), Cluster("i/2", "m/2")), TestContext.Current.CancellationToken)).Agrees);

        // A person parts a pair the rule joins in an identity the rule settles: the next pass takes the thing apart.
        var automatic = Plant();
        await Store(formbase, Second).ReplaceAsync(automatic, Of(Cluster("i/2", "m/2")), TestContext.Current.CancellationToken);
        await Store(formbase, Second).JudgeAsync(automatic, new PersonJudgment("same_item", "i/2", "m/2", Same: false, "p-7", Second.AddHours(1)), TestContext.Current.CancellationToken);
        await Store(formbase, Second.AddDays(1)).ReplaceAsync(automatic, Of(Cluster("i/2", "m/2")), TestContext.Current.CancellationToken);
        Assert.Equal([["i/1", "m/1"]], (await Store(formbase, Second).ReadAsync(TestContext.Current.CancellationToken)).Select(t => t.Members));
    }

    [Theory]
    [InlineData("other", "a", "b", "p-1")]
    [InlineData("same_item", "a", "a", "p-1")]
    [InlineData("same_item", "a", "b", " ")]
    public async Task A_judgment_of_an_undeclared_identity_one_entity_or_no_one_is_refused(string identity, string first, string second, string by)
    {
        var formbase = new FakeFormbase();

        await Assert.ThrowsAsync<IdentityStoreException>(() =>
            Store(formbase, First).JudgeAsync(Plant(), new PersonJudgment(identity, first, second, Same: true, by, First), TestContext.Current.CancellationToken));
        Assert.Empty(formbase.Type("identity_grounds").Stream);
    }

    /// <summary>D-86: what an identity holds the same as one entity is the rest of its thing — a person-settled identity only once a person said so.</summary>
    [Fact]
    public async Task The_same_as_an_entity_is_the_rest_of_its_thing_and_nothing_before_it_is_one()
    {
        var formbase = new FakeFormbase();
        var confirmed = Plant(IdentityDecision.Confirmed);
        await Store(formbase, First).ReplaceAsync(Plant(), Of(Cluster("i/1", "m/1", "m/2"), Cluster("i/2", "m/3")), TestContext.Current.CancellationToken);

        var ofItem = await Store(formbase, Second).SameAsAsync("same_item", "i/1", TestContext.Current.CancellationToken);
        Assert.Equal(["m/1", "m/2"], ofItem.Members);
        Assert.False(ofItem.Incomplete);
        Assert.Equal(["i/1", "m/2"], (await Store(formbase, Second).SameAsAsync("same_item", "m/1", TestContext.Current.CancellationToken)).Members);
        Assert.Empty((await Store(formbase, Second).SameAsAsync("same_item", "m/9", TestContext.Current.CancellationToken)).Members);

        var settled = new FakeFormbase();
        await Store(settled, First).ReplaceAsync(confirmed, Of(Cluster("i/1", "m/1")), TestContext.Current.CancellationToken);
        Assert.Empty((await Store(settled, Second).SameAsAsync("same_item", "i/1", TestContext.Current.CancellationToken)).Members);
        await Store(settled, First).JudgeAsync(confirmed, new PersonJudgment("same_item", "i/1", "m/1", Same: true, "p-7", First.AddHours(1)), TestContext.Current.CancellationToken);
        await Store(settled, Second).ReplaceAsync(confirmed, Of(Cluster("i/1", "m/1")), TestContext.Current.CancellationToken);
        Assert.Equal(["m/1"], (await Store(settled, Second).SameAsAsync("same_item", "i/1", TestContext.Current.CancellationToken)).Members);
    }

    [Fact]
    public async Task An_identity_no_longer_declared_leaves_nothing_behind()
    {
        var formbase = new FakeFormbase();
        await Store(formbase, First).ReplaceAsync(Plant(), Of(Cluster("i/1", "m/1")), TestContext.Current.CancellationToken);
        var withoutIt = Ontology.Ontology.Create([.. Plant().EntityTypes.Values]);

        var write = await Store(formbase, Second).ReplaceAsync(withoutIt, Of(), TestContext.Current.CancellationToken);

        Assert.Equal(new IdentityWrite(new(0, 0, 1), new(0, 0, 2), new(0, 0, 1)), write);
        Assert.Empty(await Store(formbase, Second).ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_cluster_of_an_undeclared_identity_or_a_form_type_of_another_shape_is_refused()
    {
        var formbase = new FakeFormbase();
        await Assert.ThrowsAsync<IdentityStoreException>(() => Store(formbase, First).ReplaceAsync(Plant(), Of(new IdentityCluster("other", ["a", "b"])), TestContext.Current.CancellationToken));

        var taken = new FakeFormbase();
        taken.Type("identities").Declaration = JsonNode.Parse("""{ "tableName": "saem_identities", "fields": [ { "name": "identity", "type": "text", "nullable": false } ] }""")!.AsObject();
        await Assert.ThrowsAsync<IdentityStoreException>(() => Store(taken, First).ReplaceAsync(Plant(), Of(), TestContext.Current.CancellationToken));
        Assert.Empty(taken.Type("identities").Stream);
    }

    [Fact]
    public async Task A_store_compared_with_what_a_pass_would_find_counts_things_by_their_members_and_writes_nothing()
    {
        var formbase = new FakeFormbase();
        await Store(formbase, First).ReplaceAsync(Plant(), Of(Cluster("i/1", "m/1"), Cluster("i/2", "m/2"), Cluster("i/3", "m/3")), TestContext.Current.CancellationToken);
        var sent = formbase.Type("identity_members").Stream.Count;

        var same = await Store(formbase, Second).CompareAsync(Plant(), Of(Cluster("i/1", "m/1"), Cluster("i/2", "m/2"), Cluster("i/3", "m/3")), TestContext.Current.CancellationToken);
        Assert.Equal(new IdentityComparison(3, 0, 0, 0, 0, 0, 0), same);
        Assert.True(same.Agrees);

        // i/1's thing grew, i/2's is gone, a new one appeared; i/3's is as held.
        var moved = await Store(formbase, Second).CompareAsync(
            Plant(), Of(Cluster("i/1", "m/1", "m/9"), Cluster("i/3", "m/3"), Cluster("i/4", "m/4")), TestContext.Current.CancellationToken);
        Assert.Equal(new IdentityComparison(Matching: 1, Missing: 1, Differing: 1, Extra: 1, GroundsMissing: 2, GroundsExtra: 1, SkippedDocuments: 0), moved);
        Assert.False(moved.Agrees);
        Assert.Equal(sent, formbase.Type("identity_members").Stream.Count);
    }

    [Fact]
    public async Task A_store_whose_projection_skipped_documents_does_not_agree()
    {
        var formbase = new FakeFormbase();
        await Store(formbase, First).ReplaceAsync(Plant(), Of(Cluster("i/1", "m/1")), TestContext.Current.CancellationToken);
        formbase.Type("identity_members").Skipped = 1;

        var comparison = await Store(formbase, Second).CompareAsync(Plant(), Of(Cluster("i/1", "m/1")), TestContext.Current.CancellationToken);

        Assert.Equal(1, comparison.SkippedDocuments);
        Assert.False(comparison.Agrees);
    }

    private static (string?, string) Shape(JsonObject declaration) =>
        ((string?)declaration["tableName"], string.Join(',', declaration["fields"]!.AsArray().Select(f => (string?)f!["name"])));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
