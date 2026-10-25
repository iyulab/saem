using System.Net.Http.Headers;
using System.Text;
using Saem.Materialization;
using Saem.Ontology;

namespace Saem.Materialization.Cli;

/// <summary>
/// The jobs an executor does: the commands of a one-shot run, each reading the group's catalog and sources afresh when it
/// starts, so a catalog replaced or a secret rotated is taken up by the next job without restarting the executor.
/// </summary>
internal static class ExecutorJobs
{
    /// <summary>The kinds a request may name, for the group <paramref name="run"/> is told.</summary>
    public static IReadOnlyDictionary<string, JobWork> For(MaterializeOptions run, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(environment);

        return new Dictionary<string, JobWork>(StringComparer.Ordinal)
        {
            // What a pass would give now against what the store holds, writing nothing.
            ["verify"] = async (output, cancellationToken) =>
            {
                var ontology = await new FileOntologyCatalog(run.CatalogPath).GetAsync(cancellationToken).ConfigureAwait(false);
                using var sources = GroupSources.Load(await File.ReadAllTextAsync(run.SourcesPath, cancellationToken).ConfigureAwait(false), environment);
                using var store = StoreClient(run);
                var agrees = await MaterializeRun.VerifyAsync(
                    ontology, sources.Openers, run.Group, new EntityIris(run.BaseIri),
                    new FormbaseEdgeStore(store, run.StoreNamespace), new FormbaseIdentityStore(store, run.StoreNamespace), output, cancellationToken).ConfigureAwait(false);
                return new JobOutcome(agrees);
            },

            // The declarations against the sources, read as the group's account; the findings come back as the report too.
            ["check"] = async (output, cancellationToken) =>
            {
                var ontology = await new FileOntologyCatalog(run.CatalogPath).GetAsync(cancellationToken).ConfigureAwait(false);
                using var sources = GroupSources.Load(await File.ReadAllTextAsync(run.SourcesPath, cancellationToken).ConfigureAwait(false), environment);
                using var report = new MemoryStream();
                var holds = await CheckRun.RunAsync(ontology, sources.Openers, output, cancellationToken, report: report).ConfigureAwait(false);
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
