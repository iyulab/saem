using System.Text.Json;
using Saem.Materialization.Cli;
using Saem.Contracts;
using Saem.Ontology;
using Saem.Query;
using Xunit;

namespace Saem.Materialization.Tests;

/// <summary><c>saem-materialize</c>: what it is told, where its secrets come from, and one group's pass into its store.</summary>
public sealed class MaterializeCliTests
{
    private static readonly string[] Complete =
    [
        "--catalog", "ontology.json", "--sources", "sources.json", "--group", "g-plant",
        "--base-iri", "https://example.org/ontology/", "--store", "http://plant.test",
    ];

    private static readonly Ontology.Ontology Plant = Ontology.Ontology.Create(
    [
        new EntityTypeDeclaration("WorkOrder", "mes", "work_orders", "wo_no", new Dictionary<string, string> { ["item"] = "item_code" }),
        new EntityTypeDeclaration("Item", "erp", "items", "item_code", new Dictionary<string, string>()),
        new RelationDeclaration("orders_item", "WorkOrder", "item", "Item"),
    ]);

    [Fact]
    public void A_complete_command_line_reads_the_store_token_from_the_environment()
    {
        var options = MaterializeOptions.Parse(Complete, v => v == "SAEM_STORE_TOKEN" ? "t0ken" : null);

        Assert.Equal(new MaterializeOptions("ontology.json", "sources.json", "g-plant", new Uri("https://example.org/ontology/"), new Uri("http://plant.test/"), null, "t0ken"), options);
    }

    [Theory]
    [InlineData("--group")]
    [InlineData("--store")]
    [InlineData("--base-iri")]
    public void A_missing_or_unknown_argument_is_a_usage_error(string left)
    {
        var without = Complete.Chunk(2).Where(p => p[0] != left).SelectMany(p => p).ToArray();

        Assert.Throws<MaterializeUsageException>(() => MaterializeOptions.Parse(without, _ => null));
        Assert.Throws<MaterializeUsageException>(() => MaterializeOptions.Parse([.. Complete, "--secret", "x"], _ => null));
        Assert.Throws<MaterializeUsageException>(() => MaterializeOptions.Parse([.. Complete, "--group", "g-office"], _ => null));
    }

    [Fact]
    public void A_sources_secret_is_named_after_its_system()
    {
        Assert.Equal("SAEM_SOURCE_SECRET_MES", MaterializeOptions.SecretVariable("mes"));
        Assert.Equal("SAEM_SOURCE_SECRET_PLANT_ERP_2", MaterializeOptions.SecretVariable("plant-erp.2"));
    }

    [Fact]
    public void Each_system_logs_in_as_the_groups_account_with_the_secret_from_the_environment()
    {
        using var sources = GroupSources.Load("""
            {
              "mes": { "postgres": "Host=mes.test;Database=mes", "user": "g_plant_pw" },
              "erp": { "mysql": "Server=erp.test;Database=erp", "user": "g_plant_pw" },
              "forms": { "morphdb": "http://forms.test", "projectId": "6f1a6f6e-0000-4000-8000-000000000001" }
            }
            """, v => v.StartsWith("SAEM_SOURCE_SECRET_", StringComparison.Ordinal) ? "s3cret" : null);

        Assert.Equal(["erp", "forms", "mes"], sources.Openers.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>Refused before anything is read: no secret set, a kind of source saem does not read, a login in the connection string, no login name, not an object of systems.</summary>
    [Theory]
    [InlineData("""{ "mes": { "postgres": "Host=mes.test", "user": "g" } }""", false)]
    [InlineData("""{ "mes": { "oracle": "x", "user": "g" } }""", true)]
    [InlineData("""{ "mes": { "postgres": "Host=mes.test;Username=root", "user": "g" } }""", true)]
    [InlineData("""{ "mes": { "postgres": "Host=mes.test" } }""", true)]
    [InlineData("""[ "mes" ]""", true)]
    public void A_source_without_its_secret_or_not_readable_is_refused_before_anything_is_read(string json, bool secretSet)
    {
        Assert.Throws<MaterializeUsageException>(() => GroupSources.Load(json, _ => secretSet ? "s3cret" : null));
    }

    [Fact]
    public async Task A_pass_writes_the_groups_edges_and_reports_what_it_did()
    {
        var formbase = new FakeFormbase();
        var output = new StringWriter();
        var openers = new Dictionary<string, SourceOpener>
        {
            ["mes"] = _ => ValueTask.FromResult<ISourceSession>(new Table([Row("WO-1", "I-100"), Row("WO-2", null)])),
        };

        var write = await MaterializeRun.RunAsync(Plant, openers, "g-plant", new EntityIris(new Uri("https://example.org/ontology/")), new FormbaseEdgeStore(formbase.Client), null, output, TestContext.Current.CancellationToken);

        Assert.Equal(new EdgeWrite(1, 0, 0), write with { ReadFrom = null });
        Assert.Equal($"group g-plant: 1 edge(s) written, 0 unchanged, 0 retired; as of {write.ReadFrom:O}.", output.ToString().Trim());
    }

    [Fact]
    public async Task A_pass_with_a_store_for_identities_files_what_each_finds_after_the_edges()
    {
        var formbase = new FakeFormbase();
        var output = new StringWriter();
        var openers = new Dictionary<string, SourceOpener>
        {
            ["mes"] = _ => ValueTask.FromResult<ISourceSession>(new Table([Row("WO-1", "I-100"), Row("WO-2", "I-100"), Row("WO-3", "I-200")])),
        };
        var duplicates = Ontology.Ontology.Create(
        [
            .. Plant.EntityTypes.Values,
            .. Plant.Relations.Values,
            new IdentityDeclaration("same_order", "WorkOrder", "WorkOrder", IdentityDecision.Automatic) { Match = [new FieldPair("item", "item")] },
        ]);

        await MaterializeRun.RunAsync(
            duplicates, openers, "g-plant", new EntityIris(new Uri("https://example.org/ontology/")), new FormbaseEdgeStore(formbase.Client), new FormbaseIdentityStore(formbase.Client), output, TestContext.Current.CancellationToken);

        Assert.EndsWith(
            "group g-plant: 1 identified thing(s) — 1 new, 0 merged, 0 gone; 2 cross-reference(s) and 1 ground(s) written.",
            output.ToString().Trim(),
            StringComparison.Ordinal);
        Assert.Equal(
            ["https://example.org/ontology/WorkOrder/WO-1", "https://example.org/ontology/WorkOrder/WO-2"],
            Assert.Single(await new FormbaseIdentityStore(formbase.Client).ReadAsync(TestContext.Current.CancellationToken)).Members);
    }

    [Fact]
    public async Task A_judgment_from_the_command_line_is_filed_as_the_persons_ground()
    {
        var formbase = new FakeFormbase();
        var options = JudgeRun.Parse(
            ["--catalog", "c.json", "--store", "http://store.test", "--identity", "same_order", "--first", "wo/2", "--second", "wo/1", "--different", "--by", "p-3"],
            name => name == "SAEM_STORE_TOKEN" ? "k" : null);
        var duplicates = Ontology.Ontology.Create(
        [
            .. Plant.EntityTypes.Values,
            new IdentityDeclaration("same_order", "WorkOrder", "WorkOrder", IdentityDecision.Automatic) { Match = [new FieldPair("item", "item")] },
        ]);
        var output = new StringWriter();

        await JudgeRun.RunAsync(duplicates, options, new FormbaseIdentityStore(formbase.Client), DateTimeOffset.UnixEpoch, output, TestContext.Current.CancellationToken);

        Assert.Equal((new Uri("http://store.test/"), "k", false), (options.Store, options.StoreToken, options.Same));
        var ground = Assert.Single(formbase.Type("identity_grounds").Rows);
        Assert.Equal("same_order wo/1 wo/2 person", ground.Key);
        Assert.Equal(("0", "p-3"), ((string?)ground.Body!["score"], (string?)ground.Body["judged_by"]));
        Assert.Equal("same_order: wo/2 and wo/1 judged two things by p-3; the next pass takes it in.", output.ToString().Trim());
    }

    [Theory]
    [InlineData(new[] { "--catalog", "c", "--store", "http://s", "--identity", "i", "--first", "a", "--second", "b", "--by", "p" }, "--same or --different is required.")]
    [InlineData(new[] { "--catalog", "c", "--store", "http://s", "--identity", "i", "--first", "a", "--second", "b", "--same", "--different", "--by", "p" }, "--same and --different are given more than once together.")]
    [InlineData(new[] { "--catalog", "c", "--store", "ftp://s", "--identity", "i", "--first", "a", "--second", "b", "--same", "--by", "p" }, "--store takes an http(s) address, not 'ftp://s'.")]
    [InlineData(new[] { "--catalog", "c", "--store", "http://s", "--identity", "i", "--first", "a", "--second", "b", "--same" }, "--by is required.")]
    public void A_judge_command_line_that_is_incomplete_is_refused(string[] args, string message)
    {
        Assert.Equal(message, Assert.Throws<MaterializeUsageException>(() => JudgeRun.Parse(args, _ => null)).Message);
    }

    /// <summary>A system missing from the sources file is found before the pass, so the store is not left half replaced.</summary>
    [Fact]
    public async Task A_relation_leaving_a_system_the_sources_do_not_name_is_refused_before_the_store_is_touched()
    {
        var formbase = new FakeFormbase();

        await Assert.ThrowsAsync<MaterializeUsageException>(() => MaterializeRun.RunAsync(
            Plant, new Dictionary<string, SourceOpener>(), "g-plant", new EntityIris(new Uri("https://example.org/ontology/")), new FormbaseEdgeStore(formbase.Client), new FormbaseIdentityStore(formbase.Client), TextWriter.Null, TestContext.Current.CancellationToken));
        Assert.Empty(formbase.Requests);
    }

    private static SourceRow Row(string key, string? item) =>
        new(key, new Dictionary<string, JsonElement> { ["item_code"] = JsonSerializer.SerializeToElement(item) });

    private sealed class Table(List<SourceRow> rows) : ISourceSession
    {
        public ValueTask<IReadOnlyList<SourceRow>> ReadAsync(string table, string keyColumn, string key, IReadOnlyList<string> columns, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<SourceRow>> FindAsync(string table, string keyColumn, string column, string value, IReadOnlyList<string> columns, int limit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<SourceRow> ScanAsync(string table, string keyColumn, IReadOnlyList<string> columns, CancellationToken cancellationToken = default) =>
            rows.ToAsyncEnumerable();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
