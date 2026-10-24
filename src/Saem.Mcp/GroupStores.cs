using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Saem.Materialization;
using Saem.Ontology;
using Saem.Query;

namespace Saem.Mcp;

/// <summary>
/// The materialized partitions a request's person reads: one per group they are in that the deployment
/// keeps a store for (saem D-22). A person in no such group reads no materialized partition at all — the
/// edges are built from what a group may see, so without a group there is nothing built for them to read.
/// </summary>
public interface IGroupStores
{
    /// <summary>
    /// How the entities the edges name are named, so an entity can be found among them; null when the
    /// deployment keeps no stores.
    /// </summary>
    EntityIris? Iris { get; }

    /// <summary>The stores of <paramref name="person"/>'s groups, in group-name order; empty when none is kept.</summary>
    IReadOnlyList<GroupStore> StoresOf(Person person);
}

/// <summary>One group's store of materialized edges.</summary>
/// <param name="Group">The group, as the token's <c>groups</c> claim names it.</param>
/// <param name="Edges">Its edges.</param>
public sealed record GroupStore(string Group, FormbaseEdgeStore Edges);

/// <summary>Where each group's materialized edges are kept, and how their entities are named.</summary>
public sealed class MaterializationOptions
{
    /// <summary>The absolute IRI the entity IRIs were minted under — the same one the materializing pass used.</summary>
    public string BaseIri { get; set; } = "";

    /// <summary>Each group's store, by the group's name in the token.</summary>
    public Dictionary<string, GroupStoreOptions> Groups { get; } = new(StringComparer.Ordinal);
}

/// <summary>One group's store: its Formbase, started over the group's own project and confined secret.</summary>
public sealed class GroupStoreOptions
{
    /// <summary>The group's Formbase.</summary>
    public string Url { get; set; } = "";

    /// <summary>The Formbase namespace, when the instance serves several; omitted, the instance's own.</summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// One of the group Formbase's access keys (its <c>Formbase:Access:Keys</c>), sent as a bearer token —
    /// omitted when the store asks for none. A store without keys is held apart only by the network it is on,
    /// so a deployment configures one; supply it from a secret store, not a checked-in file.
    /// </summary>
    public string? AccessKey { get; set; }
}

/// <summary>The stores the configuration names, each reached over its own client.</summary>
internal sealed class ConfiguredGroupStores : IGroupStores
{
    private readonly Dictionary<string, GroupStore> _stores = new(StringComparer.Ordinal);

    public ConfiguredGroupStores(IOptions<MaterializationOptions> options, IHttpClientFactory httpClients)
    {
        var materialization = options.Value;
        if (materialization.Groups.Count > 0 && !Uri.TryCreate(materialization.BaseIri, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("Saem:Materialization:BaseIri names the absolute IRI the materialized entities are named under.");
        }

        Iris = materialization.Groups.Count > 0 ? new EntityIris(new Uri(materialization.BaseIri)) : null;
        foreach (var (group, store) in materialization.Groups)
        {
            if (!Uri.TryCreate(store.Url, UriKind.Absolute, out var url))
            {
                throw new InvalidOperationException($"The group '{group}' names no store: Saem:Materialization:Groups:{group}:Url is its Formbase.");
            }

            var http = httpClients.CreateClient("Materialization:" + group);
            http.BaseAddress = url.AbsoluteUri.EndsWith('/') ? url : new Uri(url.AbsoluteUri + "/");
            if (!string.IsNullOrEmpty(store.AccessKey))
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", store.AccessKey);
            }

            _stores.Add(group, new GroupStore(group, new FormbaseEdgeStore(http, store.Namespace)));
        }
    }

    public EntityIris? Iris { get; }

    public IReadOnlyList<GroupStore> StoresOf(Person person) =>
        [.. person.Groups.Where(_stores.ContainsKey).Select(g => _stores[g])];
}

/// <summary>
/// The materialized relations one person reads: the edge stores of their groups, each asked by the IRI of
/// the entity walked back from — the same IRI the materializing pass named it by. A partition whose projection
/// skipped a document is read over the edges that stand — a lookup can then miss an answer, never state a wrong
/// one — and says so: the answer reports it incomplete, and the skip is logged for whoever runs the store.
/// </summary>
internal sealed partial class GroupRelations(IReadOnlyList<GroupStore> stores, EntityIris? iris, ILogger logger) : IMaterializedRelations
{
    public IReadOnlyList<string> Partitions { get; } = iris is null ? [] : [.. stores.Select(s => s.Group)];

    public async ValueTask<PartitionEdges> LeadingToAsync(
        string partition,
        RelationDeclaration relation,
        string toKey,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var store = stores.Single(s => s.Group == partition).Edges;
        EdgesLeadingTo read;
        try
        {
            read = await store.LeadingToAsync(relation.Name, iris!.For(relation.To, toKey), limit, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is EdgeStoreException or HttpRequestException or TaskCanceledException { InnerException: TimeoutException })
        {
            throw MaterializedRelationsException.For(partition, e);
        }

        if (read.Incomplete)
        {
            LogSkipped(logger, partition, read.SkippedDocuments);
        }

        // An edge naming its entity under another base or type was not built by this deployment's pass.
        return new PartitionEdges([.. read.Edges.Select(e => iris.KeyOf(relation.From, e.From)).OfType<string>()], read.Incomplete);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The materialized relations of the group '{Group}' are read in part: their projection skipped {Count} document(s), so a lookup walking them back may miss an answer. Rerun the group's materialization.")]
    private static partial void LogSkipped(ILogger logger, string group, int count);

    public async ValueTask<DateTimeOffset?> AsOfAsync(string partition, CancellationToken cancellationToken = default)
    {
        try
        {
            return await stores.Single(s => s.Group == partition).Edges.AsOfAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is EdgeStoreException or HttpRequestException or TaskCanceledException { InnerException: TimeoutException })
        {
            throw MaterializedRelationsException.For(partition, e);
        }
    }
}
