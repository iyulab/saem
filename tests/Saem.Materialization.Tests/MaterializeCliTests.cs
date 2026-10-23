using System.Text.Json;
using Saem.Materialization.Cli;
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

        var write = await MaterializeRun.RunAsync(Plant, openers, "g-plant", new EntityIris(new Uri("https://example.org/ontology/")), new FormbaseEdgeStore(formbase.Client), output, TestContext.Current.CancellationToken);

        Assert.Equal(new EdgeWrite(1, 0, 0), write);
        Assert.Equal("group g-plant: 1 edge(s) written, 0 unchanged, 0 retired.", output.ToString().Trim());
    }

    /// <summary>A system missing from the sources file is found before the pass, so the store is not left half replaced.</summary>
    [Fact]
    public async Task A_relation_leaving_a_system_the_sources_do_not_name_is_refused_before_the_store_is_touched()
    {
        var formbase = new FakeFormbase();

        await Assert.ThrowsAsync<MaterializeUsageException>(() => MaterializeRun.RunAsync(
            Plant, new Dictionary<string, SourceOpener>(), "g-plant", new EntityIris(new Uri("https://example.org/ontology/")), new FormbaseEdgeStore(formbase.Client), TextWriter.Null, TestContext.Current.CancellationToken));
        Assert.Empty(formbase.Requests);
    }

    private static SourceRow Row(string key, string? item) =>
        new(key, new Dictionary<string, JsonElement> { ["item_code"] = JsonSerializer.SerializeToElement(item) });

    private sealed class Table(List<SourceRow> rows) : ISourceSession
    {
        public ValueTask<IReadOnlyList<SourceRow>> ReadAsync(string table, string keyColumn, string key, IReadOnlyList<string> columns, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<SourceRow> ScanAsync(string table, string keyColumn, IReadOnlyList<string> columns, CancellationToken cancellationToken = default) =>
            rows.ToAsyncEnumerable();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
