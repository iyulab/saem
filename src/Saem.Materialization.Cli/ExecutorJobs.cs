using System.Net.Http.Headers;
using System.Text;
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
            ["fit"] = async (arguments, output, cancellationToken) =>
            {
                var identity = arguments.Identity is { Length: > 0 } name ? name : throw new MaterializeUsageException("identity is required for a fit.");
                using var group = await open(cancellationToken).ConfigureAwait(false);
                var kept = await FitRun.RunAsync(
                    group.Ontology, identity, group.Openers, run.Group, iris,
                    new FormbaseIdentityStore(group.Store, run.StoreNamespace), DateTimeOffset.UtcNow, output, cancellationToken).ConfigureAwait(false);
                return new JobOutcome(kept);
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

    private static HttpClient StoreClient(MaterializeOptions run)
    {
        var client = new HttpClient { BaseAddress = run.Store };
        if (run.StoreToken is { } token)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }
}
