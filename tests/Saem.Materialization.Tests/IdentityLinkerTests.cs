using Saem.Contracts;
using Saem.Ontology;
using Xunit;
using static Saem.Materialization.Tests.EdgeMaterializerTests;

namespace Saem.Materialization.Tests;

/// <summary>
/// saem D-87 ⓑ ⓒ: an identity by a linkage rule is fitted once to what the group reads and scored with the kept fit —
/// a rule-settled identity's things are what its match pairs join, a person-settled one's are candidate pairs only, and
/// two rows of one type are never paired when the identity joins two types.
/// </summary>
public sealed class IdentityLinkerTests
{
    private const string Base = "https://example.org/ontology/";

    private static readonly EntityIris Iris = new(new Uri(Base));

    private static readonly DateTimeOffset Now = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    internal static readonly EntityTypeDeclaration Supplier = new(
        "Supplier", "mes", "suppliers", "sup_id", new Dictionary<string, string> { ["name"] = "sup_name", ["bizNo"] = "biz_no" });

    internal static readonly EntityTypeDeclaration Vendor = new(
        "Vendor", "erp", "vendors", "vendor_id", new Dictionary<string, string> { ["name"] = "vendor_name", ["regNo"] = "reg_no" });

    internal static readonly LinkageDeclaration Names = new(
        "supplier-names",
        [new LinkageComparison("name", "name", LinkageMethod.JaroWinkler, [0.92]), new LinkageComparison("bizNo", "regNo")]);

    internal static IdentityDeclaration Same(IdentityDecision decision) => new("same_supplier", "Supplier", "Vendor", decision) { Linkage = "supplier-names" };

    private static Ontology.Ontology With(IdentityDecision decision, LinkageDeclaration? rule = null) =>
        Ontology.Ontology.Create([Supplier, Vendor, rule ?? Names, Same(decision)]);

    private static string S(int n) => Base + "Supplier/S-" + n;

    private static string V(int n) => Base + "Vendor/V-" + n;

    private static readonly string[] Companies =
    [
        "Hanbit Precision", "Daesung Metal", "Woori Hydraulics", "Seoul Bearing", "Mirae Plastics", "Kumho Castings",
        "Samil Valve", "Jinyang Coating", "Hyundai Springs", "Taeyang Gears", "Dongbang Seals", "Pohang Forging",
    ];

    // Ten suppliers each kept once more as a vendor — the same registration number, the name now and then misspelt —
    // and two of each side the other never had. One vendor shares a supplier's name but not its number.
    internal static Sessions Sources() => new(new()
    {
        ["mes/suppliers"] =
        [
            .. Enumerable.Range(0, 12).Select(n => Row($"S-{n}", ("sup_name", Companies[n]), ("biz_no", $"1{n:00}-81-{n:00000}"))),
        ],
        ["erp/vendors"] =
        [
            .. Enumerable.Range(0, 10).Select(n => Row(
                $"V-{n}",
                ("vendor_name", n % 3 == 0 ? Companies[n].Replace('a', 'e') : Companies[n]),
                ("reg_no", $"1{n:00}-81-{n:00000}"))),
            Row("V-10", ("vendor_name", "Hanbit Precision"), ("reg_no", "999-81-99999")),
            Row("V-11", ("vendor_name", "Gwangju Tooling"), ("reg_no", "998-81-99998")),
        ],
    });

    private static async Task<LinkageFit> FitAsync(Ontology.Ontology ontology) =>
        await new IdentityLinker(ontology, Iris).FitAsync(Sources(), "same_supplier", Now, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_rule_settled_identity_is_the_things_its_match_pairs_join_each_of_both_types()
    {
        var ontology = With(IdentityDecision.Automatic);
        var fit = await FitAsync(ontology);
        var skipped = new List<LinkageSkip>();

        var clusters = await new IdentityLinker(ontology, Iris)
            .ScoreAsync(Sources(), new Dictionary<string, LinkageFit> { ["same_supplier"] = fit }, skipped.Add, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(skipped);
        Assert.Equal(24, fit.Records);
        // S-6 and S-9's vendors are spelt past the name's threshold: the number alone leaves them undecided, so a rule
        // that settles alone joins neither.
        Assert.Equal(
            [.. Enumerable.Range(0, 9).Where(n => n != 6).Select(n => new IdentityCluster("same_supplier", [S(n), V(n)]))],
            clusters);

        // Each thing stands on the pair the rule scored, named by the rule; a vendor that only shares a name is not one.
        var ground = Assert.Single(clusters[0].Grounds!);
        Assert.Equal("supplier-names", ground.Rule);
        Assert.True(ground.Score >= 4, $"a match scores at or above the match threshold, not {ground.Score}");
        Assert.DoesNotContain(clusters, c => c.Members.Contains(V(10)) || c.Members.Contains(V(11)));
    }

    [Fact]
    public async Task A_person_settled_identity_gives_each_scored_pair_as_a_candidate_and_no_thing()
    {
        var ontology = With(IdentityDecision.Confirmed);
        var fit = await FitAsync(ontology);

        var candidates = await new IdentityLinker(ontology, Iris)
            .ScoreAsync(Sources(), new Dictionary<string, LinkageFit> { ["same_supplier"] = fit }, cancellationToken: TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(candidates, c => Assert.Equal(2, c.Members.Count));
        Assert.All(candidates, c => Assert.Single(c.Grounds!));
        // The undecided pairs a rule-settled identity drops are a person's to judge here.
        Assert.Superset(
            Enumerable.Range(0, 10).Select(n => $"{S(n)} {V(n)}").ToHashSet(),
            candidates.Select(c => $"{c.Members[0]} {c.Members[1]}").ToHashSet());

        // The rule's candidates make nothing one thing until a person says so.
        Assert.Empty(IdentityJudgments.Apply(ontology.Identities["same_supplier"], candidates, []));
    }

    [Fact]
    public async Task Two_rows_of_one_type_are_never_scored_against_each_other_when_the_identity_joins_two()
    {
        var ontology = With(IdentityDecision.Confirmed, Names with { NonMatchThreshold = -1000, MatchThreshold = 1000 });
        var fit = await FitAsync(ontology);

        var pairs = await new IdentityLinker(ontology, Iris)
            .ScoreAsync(Sources(), new Dictionary<string, LinkageFit> { ["same_supplier"] = fit }, cancellationToken: TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Every pair is undecided under these thresholds, so every compared pair is a candidate: 12 × 12, none within a type.
        Assert.Equal(144, pairs.Count);
        Assert.All(pairs, p => Assert.NotEqual(p.Members[0].Contains("/Supplier/", StringComparison.Ordinal), p.Members[1].Contains("/Supplier/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_pass_leaves_out_an_identity_with_no_fit_or_a_fit_of_another_rule()
    {
        var ontology = With(IdentityDecision.Automatic);
        var fit = await FitAsync(ontology);
        var changed = With(IdentityDecision.Automatic, Names with { Compare = [new LinkageComparison("name", "name", LinkageMethod.JaroWinkler, [0.95])] });
        var skipped = new List<LinkageSkip>();
        var sources = Sources();

        Assert.Empty(await new IdentityLinker(ontology, Iris).ScoreAsync(sources, new Dictionary<string, LinkageFit>(), skipped.Add, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await new IdentityLinker(changed, Iris).ScoreAsync(sources, new Dictionary<string, LinkageFit> { ["same_supplier"] = fit }, skipped.Add, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            [
                new LinkageSkip("same_supplier", "no fit is kept for it — run the fit first"),
                new LinkageSkip("same_supplier", "its fit was made for a rule declared otherwise — fit it again"),
            ],
            skipped);
        Assert.Empty(sources.Scans);

        // Who settles the identity is not part of what a fit stands for.
        Assert.Equal(fit.Rule, IdentityLinker.RuleOf(Same(IdentityDecision.Confirmed), Names));
    }

    [Fact]
    public async Task Fitting_reads_the_compared_columns_only_and_refuses_an_identity_by_fields()
    {
        var sources = Sources();
        await new IdentityLinker(With(IdentityDecision.Automatic), Iris).FitAsync(sources, "same_supplier", Now, TestContext.Current.CancellationToken);

        Assert.Equal(["mes suppliers sup_id [sup_name,biz_no]", "erp vendors vendor_id [vendor_name,reg_no]"], sources.Scans);

        var byFields = Ontology.Ontology.Create(
            [Supplier, Vendor, new IdentityDeclaration("same", "Supplier", "Vendor", IdentityDecision.Automatic) { Match = [new FieldPair("bizNo", "regNo")] }]);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new IdentityLinker(byFields, Iris).FitAsync(sources, "same", Now, TestContext.Current.CancellationToken));
    }
}
