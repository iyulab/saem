using System.Net;
using Eyu.Core.Declared;
using Eyu.Core.Grounding;
using Eyu.Core.Inference;
using Eyu.Core.Ports;
using Eyu.Core.Primitives;
using Eyu.Core.Proposals;
using Eyu.Core.Records;
using Saem.Authoring.Cli;
using Saem.Connectors.Formbase;
using Saem.Ontology;
using Xunit;

namespace Saem.Authoring.Tests;

/// <summary>
/// The authoring command: what its command line takes, and a run from files to files — the review and the
/// draft written, nothing replaced unasked, retried model calls reported in the review.
/// </summary>
public sealed class AuthorCommandTests : IDisposable
{
    private static readonly string[] Required =
    [
        "--formbase", "http://forms.test", "--sources", "sources.json", "--catalog", "ontology.json",
        "--model-endpoint", "https://model.test/v1", "--model", "m",
    ];

    private readonly string _dir = Directory.CreateTempSubdirectory("saem-author-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void The_required_arguments_alone_take_the_defaults()
    {
        var options = AuthorOptions.Parse(Required, _ => null);

        Assert.Equal(new Uri("http://forms.test/"), options.Formbase);
        Assert.Equal(new Uri("https://model.test/v1/"), options.ModelEndpoint);
        Assert.Equal(("m", 1, 20, TimeSpan.FromMinutes(10)), (options.Model, options.Retries, options.RecordsPerSubject, options.ModelTimeout));
        Assert.Equal(("review.md", "draft.json", "proposals.json"), (options.ReviewPath, options.DraftPath, options.ProposalsPath));
        Assert.False(options.Overwrite);
        Assert.False(options.ModelStream);
        Assert.Equal(SampleDirection.Newest, options.Sample);
        Assert.Null(options.FormbaseNamespace);
        Assert.Null(options.ModelApiKey);
        Assert.Null(options.FormbaseToken);
    }

    [Fact]
    public void Secrets_come_from_the_environment()
    {
        var environment = new Dictionary<string, string> { [AuthorOptions.ModelApiKeyVariable] = "k", [AuthorOptions.FormbaseTokenVariable] = "t" };

        var options = AuthorOptions.Parse(Required, name => environment.GetValueOrDefault(name));

        Assert.Equal(("k", "t"), (options.ModelApiKey, options.FormbaseToken));
    }

    [Fact]
    public void Options_and_flags_are_read()
    {
        var options = AuthorOptions.Parse(
            [.. Required, "--formbase-namespace", "plant", "--model-stream", "--retries", "0", "--records", "5",
             "--model-timeout-minutes", "3", "--review", "r.md", "--draft", "ontology.json", "--overwrite"],
            _ => null);

        Assert.Equal(("plant", true, 0, 5, TimeSpan.FromMinutes(3)), (options.FormbaseNamespace, options.ModelStream, options.Retries, options.RecordsPerSubject, options.ModelTimeout));
        Assert.Equal(("r.md", "ontology.json", true), (options.ReviewPath, options.DraftPath, options.Overwrite));
    }

    [Theory]
    [InlineData("--model", "--model is required.")]
    [InlineData("--catalog", "--catalog is required.")]
    public void A_missing_required_argument_is_named(string left, string message)
    {
        var args = new List<string>(Required);
        args.RemoveRange(args.IndexOf(left), 2);

        Assert.Equal(message, Assert.Throws<AuthorUsageException>(() => AuthorOptions.Parse(args, _ => null)).Message);
    }

    [Theory]
    [InlineData(new[] { "--records", "0" }, "--records takes a whole number of at least 1, not '0'.")]
    [InlineData(new[] { "--retries", "-1" }, "--retries takes a whole number of at least 0, not '-1'.")]
    [InlineData(new[] { "--records" }, "--records needs a value.")]
    [InlineData(new[] { "--sample", "latest" }, "--sample takes newest or oldest, not 'latest'.")]
    [InlineData(new[] { "--sample" }, "--sample needs a value.")]
    [InlineData(new[] { "--records", "--overwrite" }, "--records needs a value.")]
    [InlineData(new[] { "--api-key", "k" }, "Unknown argument '--api-key'.")]
    [InlineData(new[] { "--model", "n" }, "--model is given more than once.")]
    [InlineData(new[] { "--formbase", "forms.test" }, "--formbase is given more than once.")]
    public void A_malformed_command_line_is_refused(string[] extra, string message) =>
        Assert.Equal(message, Assert.Throws<AuthorUsageException>(() => AuthorOptions.Parse([.. Required, .. extra], _ => null)).Message);

    [Theory]
    [InlineData("newest", SampleDirection.Newest)]
    [InlineData("oldest", SampleDirection.Oldest)]
    public void The_end_of_the_stream_a_sample_is_taken_from_is_chosen(string value, SampleDirection expected) =>
        Assert.Equal(expected, AuthorOptions.Parse([.. Required, "--sample", value], _ => null).Sample);

    [Fact]
    public void An_address_must_be_http()
    {
        var args = new List<string>(Required);
        args[args.IndexOf("http://forms.test")] = "forms.test";

        Assert.Equal("--formbase takes an http(s) address, not 'forms.test'.", Assert.Throws<AuthorUsageException>(() => AuthorOptions.Parse(args, _ => null)).Message);
    }

    [Fact]
    public async Task A_run_writes_the_review_and_the_draft_over_the_declarations_in_force()
    {
        var options = await PrepareAsync(catalog: """[{"kind":"entityType","name":"Item","system":"erp","table":"items","key":"item_code","fields":{"name":"item_name"}}]""");
        var log = new StringWriter();

        await AuthorRun.RunAsync(options, new Proposer(), new Sampler(), new NoStructures(), [], log, TestContext.Current.CancellationToken);

        var draft = await File.ReadAllTextAsync(options.DraftPath, TestContext.Current.CancellationToken);
        var declarations = Ontology.Ontology.Create(System.Text.Json.JsonSerializer.Deserialize(draft, OntologyJson.Default.IReadOnlyListDeclaration)!);
        Assert.Equal(["Item", "WorkOrder"], declarations.EntityTypes.Keys.Order(StringComparer.Ordinal));
        var review = await File.ReadAllTextAsync(options.ReviewPath, TestContext.Current.CancellationToken);
        Assert.StartsWith("# Ontology draft for review", review, StringComparison.Ordinal);
        Assert.DoesNotContain("## Model calls retried", review, StringComparison.Ordinal);
        Assert.Contains("0 model calls retried", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_records_what_it_was_made_with_in_its_proposals_and_its_review()
    {
        var ct = TestContext.Current.CancellationToken;
        var facts = new ProposerFacts("0.23.0", "a1b2c3d4", "e5f6a7b8");
        var options = await PrepareAsync(catalog: """[{"kind":"entityType","name":"Item","system":"erp","table":"items","key":"item_code","fields":{"name":"item_name"}}]""");
        ModelCallRetry[] retried = [new(new ModelRequest("p"), 1, new TimeoutException("slow"))];

        await AuthorRun.RunAsync(options, new Proposer(), new Sampler(), new NoStructures(), retried, TextWriter.Null, ct, facts);

        var generation = ProposedDeclarations.Parse(await File.ReadAllTextAsync(options.ProposalsPath, ct)).Generation;
        Assert.NotNull(generation);
        Assert.Equal((options.Model, "0.23.0", "a1b2c3d4", "e5f6a7b8", 1), (generation.Model, generation.ProposerVersion, generation.PromptFingerprint, generation.KnownTypesPromptFingerprint, generation.ModelCallsRetried));
        var review = await File.ReadAllTextAsync(options.ReviewPath, ct);
        Assert.Contains("## Generated with", review, StringComparison.Ordinal);
        Assert.Contains($"- model: {options.Model}", review, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_catalog_that_declares_no_types_means_no_known_types_wording_in_the_record()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = await PrepareAsync(catalog: null);

        await AuthorRun.RunAsync(options, new Proposer(), new Sampler(), new NoStructures(), [], TextWriter.Null, ct, new ProposerFacts("0.23.0", "a1b2c3d4", "e5f6a7b8"));

        Assert.Null(ProposedDeclarations.Parse(await File.ReadAllTextAsync(options.ProposalsPath, ct)).Generation!.KnownTypesPromptFingerprint);
    }

    [Fact]
    public async Task A_run_over_a_proposer_it_knows_nothing_about_records_no_generation()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = await PrepareAsync(catalog: null);

        await AuthorRun.RunAsync(options, new Proposer(), new Sampler(), new NoStructures(), [], TextWriter.Null, ct);

        Assert.Null(ProposedDeclarations.Parse(await File.ReadAllTextAsync(options.ProposalsPath, ct)).Generation);
        Assert.DoesNotContain("## Generated with", await File.ReadAllTextAsync(options.ReviewPath, ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_keeps_its_proposals_and_settle_records_what_the_review_took()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = await PrepareAsync(catalog: null);
        await AuthorRun.RunAsync(options, new Proposer(), new Sampler(), new NoStructures(), [], TextWriter.Null, ct);
        var proposed = ProposedDeclarations.Parse(await File.ReadAllTextAsync(options.ProposalsPath, ct));
        Assert.StartsWith("pass-", proposed.Pass, StringComparison.Ordinal);
        Assert.Contains(proposed.Added, d => d.Name == "WorkOrder");

        // The review: the draft becomes the catalog as it was proposed.
        File.Copy(options.DraftPath, options.CatalogPath, overwrite: true);
        var records = Path.Combine(_dir, "settled.jsonl");
        var settle = new SettleOptions(options.ProposalsPath, options.CatalogPath, records, "r-1");
        var log = new StringWriter();
        await SettleRun.RunAsync(settle, DateTimeOffset.UnixEpoch.AddDays(1), log, ct);

        var lines = await File.ReadAllLinesAsync(records, ct);
        Assert.Equal(proposed.Added.Count, lines.Length);
        Assert.All(lines, line => Assert.Contains("\"taken\":\"AcceptOther\"", line, StringComparison.Ordinal));
        Assert.Contains($"{lines.Length} proposals of {proposed.Pass} settled ({lines.Length} taken)", log.ToString(), StringComparison.Ordinal);

        // The same pass settled twice would count its review twice.
        var again = await Assert.ThrowsAsync<AuthorRunException>(() => SettleRun.RunAsync(settle, DateTimeOffset.UnixEpoch.AddDays(2), TextWriter.Null, ct));
        Assert.Contains("already recorded", again.Message, StringComparison.Ordinal);
        Assert.Equal(lines.Length, (await File.ReadAllLinesAsync(records, ct)).Length);
    }

    [Theory]
    [InlineData(new[] { "--proposals", "p.json", "--catalog", "c.json" }, "--records is required.")]
    [InlineData(new[] { "--proposals", "p.json", "--catalog", "c.json", "--records", "r.jsonl", "--by", "x" }, "Unknown argument '--by'.")]
    [InlineData(new[] { "--proposals", "--catalog", "c.json" }, "--proposals needs a value.")]
    public void A_malformed_settle_command_line_is_refused(string[] args, string message) =>
        Assert.Equal(message, Assert.Throws<AuthorUsageException>(() => SettleOptions.Parse(args)).Message);

    [Fact]
    public void A_settle_command_line_is_read()
    {
        var options = SettleOptions.Parse(["--proposals", "p.json", "--catalog", "c.json", "--records", "r.jsonl", "--settled-by", "r-1"]);

        Assert.Equal(new SettleOptions("p.json", "c.json", "r.jsonl", "r-1"), options);
    }

    [Fact]
    public async Task Settle_reads_the_catalog_the_review_left_and_refuses_a_missing_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var proposals = Path.Combine(_dir, "proposals.json");
        await File.WriteAllTextAsync(proposals, new ProposedDeclarations("pass-x", []).ToJson(), ct);

        var refused = await Assert.ThrowsAsync<AuthorRunException>(() =>
            SettleRun.RunAsync(new SettleOptions(proposals, Path.Combine(_dir, "none.json"), Path.Combine(_dir, "r.jsonl"), null), DateTimeOffset.UnixEpoch, TextWriter.Null, ct));

        Assert.Contains("does not exist", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_catalog_is_an_empty_ontology()
    {
        var options = await PrepareAsync(catalog: null);

        await AuthorRun.RunAsync(options, new Proposer(), new Sampler(), new NoStructures(), [], TextWriter.Null, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(options.DraftPath));
    }

    [Fact]
    public async Task An_existing_output_is_not_replaced_unless_told_and_nothing_is_read_first()
    {
        var options = await PrepareAsync(catalog: null);
        await File.WriteAllTextAsync(options.DraftPath, "kept", TestContext.Current.CancellationToken);
        var sampler = new Sampler();

        var refused = await Assert.ThrowsAsync<AuthorRunException>(() =>
            AuthorRun.RunAsync(options, new Proposer(), sampler, new NoStructures(), [], TextWriter.Null, TestContext.Current.CancellationToken));

        Assert.Contains("pass --overwrite to replace it", refused.Message, StringComparison.Ordinal);
        Assert.Equal("kept", await File.ReadAllTextAsync(options.DraftPath, TestContext.Current.CancellationToken));
        Assert.Equal(0, sampler.Calls);

        await AuthorRun.RunAsync(options with { Overwrite = true }, new Proposer(), sampler, new NoStructures(), [], TextWriter.Null, TestContext.Current.CancellationToken);
        Assert.NotEqual("kept", await File.ReadAllTextAsync(options.DraftPath, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{}", "names no subject")]
    [InlineData("""{"work_orders":{"system":"mes","table":"work_orders"}}""", "'work_orders' needs a system, a table and a keyField")]
    [InlineData("[", "cannot be read")]
    public async Task A_sources_file_that_cannot_place_a_subject_is_refused(string sources, string message)
    {
        var options = await PrepareAsync(catalog: null, sources: sources);

        var refused = await Assert.ThrowsAsync<AuthorRunException>(() =>
            AuthorRun.RunAsync(options, new Proposer(), new Sampler(), new NoStructures(), [], TextWriter.Null, TestContext.Current.CancellationToken));

        Assert.Contains(message, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retried_model_calls_are_reported_in_the_review()
    {
        var options = await PrepareAsync(catalog: null);
        var retries = new List<ModelCallRetry> { new(new ModelRequest("p"), 1, new HttpRequestException("stream cut", null, HttpStatusCode.BadGateway)) };

        await AuthorRun.RunAsync(options, new Proposer(), new Sampler(), new NoStructures(), retries, TextWriter.Null, TestContext.Current.CancellationToken);

        var review = await File.ReadAllTextAsync(options.ReviewPath, TestContext.Current.CancellationToken);
        Assert.Contains("## Model calls retried", review, StringComparison.Ordinal);
        Assert.Contains("1 model call attempts failed transiently and were tried again", review, StringComparison.Ordinal);
        Assert.Contains("- attempt 1: HttpRequestException: stream cut", review, StringComparison.Ordinal);
    }

    private async Task<AuthorOptions> PrepareAsync(string? catalog, string sources = """{"work_orders":{"system":"mes","table":"work_orders","keyField":"wo_no"}}""")
    {
        var ct = TestContext.Current.CancellationToken;
        var catalogPath = Path.Combine(_dir, "ontology.json");
        if (catalog is not null)
        {
            await File.WriteAllTextAsync(catalogPath, catalog, ct);
        }

        var sourcesPath = Path.Combine(_dir, "sources.json");
        await File.WriteAllTextAsync(sourcesPath, sources, ct);
        return AuthorOptions.Parse(Required, _ => null) with
        {
            SourcesPath = sourcesPath,
            CatalogPath = catalogPath,
            ReviewPath = Path.Combine(_dir, "review.md"),
            DraftPath = Path.Combine(_dir, "draft.json"),
            ProposalsPath = Path.Combine(_dir, "proposals.json"),
        };
    }

    [Fact]
    public async Task Diff_writes_the_breaking_changes_first_and_says_whether_none_breaks()
    {
        var ct = TestContext.Current.CancellationToken;
        const string item = """{ "kind": "entityType", "name": "Item", "system": "erp", "table": "items", "key": "item_code", "fields": { "name": "item_name" } }""";
        const string order = """{ "kind": "entityType", "name": "WorkOrder", "system": "mes", "table": "work_orders", "key": "wo_no", "fields": { "item": "item_code" } }""";
        var before = Path.Combine(_dir, "before.json");
        var added = Path.Combine(_dir, "added.json");
        var broken = Path.Combine(_dir, "broken.json");
        await File.WriteAllTextAsync(before, $"[{item}]", ct);
        await File.WriteAllTextAsync(added, $"[{item}, {order}]", ct);
        await File.WriteAllTextAsync(broken, $"[{order}]", ct);

        var log = new StringWriter();
        Assert.True(await DiffRun.RunAsync(new DiffOptions(before, added), log, ct));
        Assert.EndsWith("0 breaking, 1 additive.\n  additive  entityType WorkOrder: added", log.ToString().Trim().ReplaceLineEndings("\n"), StringComparison.Ordinal);

        log = new StringWriter();
        Assert.False(await DiffRun.RunAsync(new DiffOptions(before, broken), log, ct));
        Assert.Equal(
            ["  breaking  entityType Item: removed", "  additive  entityType WorkOrder: added"],
            log.ToString().Trim().ReplaceLineEndings("\n").Split('\n')[1..]);
    }

    [Theory]
    [InlineData(new[] { "--before", "a.json" }, "--after is required.")]
    [InlineData(new[] { "--before", "a.json", "--before", "b.json" }, "--before is given more than once.")]
    [InlineData(new[] { "--after" }, "--after needs a value.")]
    [InlineData(new[] { "--catalog", "a.json" }, "Unknown argument '--catalog'.")]
    public void A_diff_command_line_that_is_incomplete_is_refused(string[] args, string message)
    {
        Assert.Equal(message, Assert.Throws<AuthorUsageException>(() => DiffOptions.Parse(args)).Message);
    }

    private sealed class Proposer : IOntologyProposer
    {
        public Task<OntologyProposal> ProposeAsync(IReadOnlyList<DeclaredStructure> declaredStructures, IReadOnlyList<RawRecord> records, PriorKnowledge prior, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OntologyProposal(
                [EntityProposal.Create("e1", "WO-1", "WorkOrder", GroundedClaim.Create("work order", [new SourceRef("wo-1")]), VocabularyOrigin.Acquired, 0.9)],
                [],
                []));
    }

    private sealed class Sampler : IRecordSample
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<RawRecord>> SampleAsync(SubjectRef subject, int maxCount, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<RawRecord>>([new("wo-1", new Dictionary<string, string?> { ["wo_no"] = "WO-1" })]);
        }
    }

    private sealed class NoStructures : IStructureSource
    {
        public Task<DeclaredStructure?> GetStructureAsync(SubjectRef subject, CancellationToken cancellationToken = default) =>
            Task.FromResult<DeclaredStructure?>(null);
    }
}
