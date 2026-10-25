using System.Text;
using System.Text.Json;
using Saem.Ontology;
using Saem.Query;

namespace Saem.Materialization.Cli;

/// <summary>What one job works on: the declarations in force, how the group's account reads each source, and the group's store.</summary>
internal sealed class GroupContext(Ontology.Ontology ontology, IReadOnlyDictionary<string, SourceOpener> openers, HttpClient store, IDisposable? owned = null) : IDisposable
{
    public Ontology.Ontology Ontology { get; } = ontology;

    public IReadOnlyDictionary<string, SourceOpener> Openers { get; } = openers;

    public HttpClient Store { get; } = store;

    public void Dispose()
    {
        Store.Dispose();
        owned?.Dispose();
    }
}

/// <summary>Opens what a job works on, afresh: a catalog replaced or a secret rotated since the last job is taken up by this one.</summary>
internal delegate Task<GroupContext> OpenGroup(CancellationToken cancellationToken);

/// <summary>
/// The jobs an executor does: the commands of a one-shot run, each reading the group's catalog and sources afresh when it
/// starts, so a catalog replaced or a secret rotated is taken up by the next job without restarting the executor.
/// </summary>
internal static class ExecutorJobs
{
    /// <summary>The kinds a request may name, for the group <paramref name="run"/> is told — its catalog and sources read from the files it names.</summary>
    public static IReadOnlyDictionary<string, JobWork> ForFiles(MaterializeOptions run, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(environment);

        return For(run, async cancellationToken =>
        {
            var ontology = await new FileOntologyCatalog(run.CatalogPath).GetAsync(cancellationToken).ConfigureAwait(false);
            var sources = GroupSources.Load(await File.ReadAllTextAsync(run.SourcesPath, cancellationToken).ConfigureAwait(false), environment);
            return new GroupContext(ontology, sources.Openers, StoreClient(run), sources);
        });
    }

    /// <summary>The kinds a request may name, working on what <paramref name="open"/> gives each job.</summary>
    public static IReadOnlyDictionary<string, JobWork> For(MaterializeOptions run, OpenGroup open)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(open);

        var iris = new EntityIris(run.BaseIri);

        async Task<JobOutcome> PassAsync(bool changedOnly, TextWriter output, CancellationToken cancellationToken)
        {
            using var group = await open(cancellationToken).ConfigureAwait(false);
            await MaterializeRun.RunAsync(
                group.Ontology, group.Openers, run.Group, iris,
                new FormbaseEdgeStore(group.Store, run.StoreNamespace), new FormbaseIdentityStore(group.Store, run.StoreNamespace), output, cancellationToken, changedOnly).ConfigureAwait(false);
            return new JobOutcome(true);
        }

        return new Dictionary<string, JobWork>(StringComparer.Ordinal)
        {
            // What a pass would give now against what the store holds, writing nothing.
            ["verify"] = async (_, output, cancellationToken) =>
            {
                using var group = await open(cancellationToken).ConfigureAwait(false);
                var agrees = await MaterializeRun.VerifyAsync(
                    group.Ontology, group.Openers, run.Group, iris,
                    new FormbaseEdgeStore(group.Store, run.StoreNamespace), new FormbaseIdentityStore(group.Store, run.StoreNamespace), output, cancellationToken).ConfigureAwait(false);
                return new JobOutcome(agrees);
            },

            // A pass: the group's edges, then what its identities find, made to be what the sources give now.
            ["pass"] = (_, output, cancellationToken) => PassAsync(changedOnly: false, output, cancellationToken),

            // The same, reading only the rows changed since the last pass (saem-materialize --help, --changed).
            ["changed-pass"] = (_, output, cancellationToken) => PassAsync(changedOnly: true, output, cancellationToken),

            // An identity's linkage rule fitted to the group's records, the fit kept in the store.
            ["fit"] = async (call, output, cancellationToken) =>
            {
                var identity = Required(call.Arguments.Identity, "identity");
                using var group = await open(cancellationToken).ConfigureAwait(false);
                var kept = await FitRun.RunAsync(
                    group.Ontology, identity, group.Openers, run.Group, iris,
                    new FormbaseIdentityStore(group.Store, run.StoreNamespace), DateTimeOffset.UtcNow, output, cancellationToken).ConfigureAwait(false);
                return new JobOutcome(kept);
            },

            // A person's judgment of a pair an identity is about, filed in the store; the requester is the person.
            ["judge"] = async (call, output, cancellationToken) =>
            {
                var arguments = call.Arguments;
                var judgment = new JudgeOptions(
                    run.CatalogPath, run.Store, run.StoreNamespace, run.StoreToken,
                    Required(arguments.Identity, "identity"), Required(arguments.First, "first"), Required(arguments.Second, "second"),
                    arguments.Same ?? throw new MaterializeUsageException("same is required for a judgment: true when the pair is one thing, false when it is two."),
                    call.RequestedBy);
                using var group = await open(cancellationToken).ConfigureAwait(false);
                await JudgeRun.RunAsync(group.Ontology, judgment, new FormbaseIdentityStore(group.Store, run.StoreNamespace), DateTimeOffset.UtcNow, output, cancellationToken).ConfigureAwait(false);
                return new JobOutcome(true);
            },

            // The pairs people have yet to judge, read from the store: IRIs and scores, no value any record holds.
            ["candidates"] = async (_, output, cancellationToken) =>
            {
                using var group = await open(cancellationToken).ConfigureAwait(false);
                var pairs = await new FormbaseIdentityStore(group.Store, run.StoreNamespace).CandidatesAsync(group.Ontology, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"group {run.Group}: {pairs.Count} pair(s) await a person's judgment.").ConfigureAwait(false);
                return new JobOutcome(true, JsonSerializer.Serialize(new { candidates = pairs.Select(pair => Candidate(group.Ontology, iris, pair)) }, CandidateJson));
            },

            // What the store holds, in counts and times: when the last pass began reading, each relation's edges and watermark, each identity's things.
            ["status"] = async (_, output, cancellationToken) =>
            {
                using var group = await open(cancellationToken).ConfigureAwait(false);
                var status = await GroupStatus.ReadAsync(group.Ontology, new FormbaseEdgeStore(group.Store, run.StoreNamespace), new FormbaseIdentityStore(group.Store, run.StoreNamespace), cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"group {run.Group}: {status.Relations.Sum(r => r.Edges)} edge(s) over {status.Relations.Count} relation(s), {status.Identities.Sum(i => i.Things)} thing(s) over {status.Identities.Count} identity(ies).").ConfigureAwait(false);
                return new JobOutcome(true, JsonSerializer.Serialize(new { status }, CandidateJson));
            },

            // The declarations against the sources, read as the group's account; the findings come back as the report too.
            ["check"] = async (_, output, cancellationToken) =>
            {
                using var group = await open(cancellationToken).ConfigureAwait(false);
                using var report = new MemoryStream();
                var holds = await CheckRun.RunAsync(group.Ontology, group.Openers, output, cancellationToken, report: report).ConfigureAwait(false);
                return new JobOutcome(holds, Encoding.UTF8.GetString(report.ToArray()));
            },
        };
    }

    /// <summary>A pair as the report gives it: the IRIs, and each entity by its type and key too, so a reader need not take an IRI apart.</summary>
    private static object Candidate(Ontology.Ontology ontology, EntityIris iris, PairCandidate pair)
    {
        var identity = ontology.Identities[pair.Identity];
        object? Entity(string iri) => new[] { identity.First, identity.Second }.Distinct(StringComparer.Ordinal)
            .Select(type => iris.KeyOf(type, iri) is { } key ? new { type, key } : null)
            .FirstOrDefault(found => found is not null);
        return new { pair.Identity, pair.First, pair.Second, pair.Rule, pair.Score, FirstEntity = Entity(pair.First), SecondEntity = Entity(pair.Second) };
    }

    private static readonly JsonSerializerOptions CandidateJson = new(JsonSerializerDefaults.Web);

    private static string Required(string? value, string name) =>
        value is { Length: > 0 } ? value : throw new MaterializeUsageException($"{name} is required for this kind of job.");

    private static HttpClient StoreClient(MaterializeOptions run) => GroupStore.Client(run.Store, run.StoreToken);
}
