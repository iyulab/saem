using Microsoft.Extensions.Options;
using Saem.Materialization;

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
            _stores.Add(group, new GroupStore(group, new FormbaseEdgeStore(http, store.Namespace)));
        }
    }

    public EntityIris? Iris { get; }

    public IReadOnlyList<GroupStore> StoresOf(Person person) =>
        [.. person.Groups.Where(_stores.ContainsKey).Select(g => _stores[g])];
}
