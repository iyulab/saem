using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Saem.Ontology;

namespace Saem.Mcp;

/// <summary>
/// At start, says what the deployment cannot answer: a system the ontology declares types on that no source is
/// configured for (<see cref="SourceOptions"/>), or a catalog that cannot be read. It warns rather than refuses to
/// start — the catalog is read again on every lookup, so it may be put right without a restart, and a lookup that
/// reaches such a system is still answered as a tool error naming it.
/// </summary>
internal sealed partial class CatalogSourcesCheck(IOntologyCatalog catalog, IOptions<SourceOptions> sources, ILogger<CatalogSourcesCheck> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Ontology.Ontology ontology;
        try
        {
            ontology = await catalog.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OntologyDeclarationException e)
        {
            LogUnreadable(logger, e.Message);
            return;
        }

        var configured = sources.Value.Postgres.Keys.Concat(sources.Value.MySql.Keys).Concat(sources.Value.MorphDb.Keys).ToHashSet(StringComparer.Ordinal);
        foreach (var system in ontology.EntityTypes.Values.Select(t => t.System).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!configured.Contains(system))
            {
                LogUnconfigured(logger, system, string.Join(", ", ontology.EntityTypes.Values.Where(t => t.System == system).Select(t => t.Name).Order(StringComparer.Ordinal)));
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "The ontology declares {Types} on '{System}', which no source is configured for (Saem:Sources): lookups that reach it are answered as errors.")]
    private static partial void LogUnconfigured(ILogger logger, string system, string types);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The ontology cannot be read, so every lookup is answered as an error until it can: {Problem}")]
    private static partial void LogUnreadable(ILogger logger, string problem);
}
