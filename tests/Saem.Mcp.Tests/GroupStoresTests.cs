using Microsoft.Extensions.Options;
using Xunit;

namespace Saem.Mcp.Tests;

/// <summary>Which materialized partitions a person reads: their groups' stores, and none without a group.</summary>
public class GroupStoresTests
{
    private sealed class Clients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static ConfiguredGroupStores Stores(Action<MaterializationOptions> configure)
    {
        var options = new MaterializationOptions { BaseIri = "https://example.org/ontology/" };
        configure(options);
        return new ConfiguredGroupStores(Options.Create(options), new Clients());
    }

    private static ConfiguredGroupStores PlantAndOffice() => Stores(o =>
    {
        o.Groups["g-plant"] = new GroupStoreOptions { Url = "http://plant.test" };
        o.Groups["g-office"] = new GroupStoreOptions { Url = "http://office.test/" };
    });

    [Fact]
    public void A_person_reads_the_store_of_each_group_they_are_in_and_no_other()
    {
        var stores = PlantAndOffice();

        Assert.Equal(["g-plant"], stores.StoresOf(new Person("alice", null) { Groups = ["g-plant"] }).Select(s => s.Group));
        Assert.Equal(["g-office", "g-plant"], stores.StoresOf(new Person("carol", null) { Groups = ["g-office", "g-plant"] }).Select(s => s.Group));
    }

    /// <summary>T-41: a person no group maps reads no materialized partition — not a default one, not all of them.</summary>
    [Fact]
    public void A_person_in_no_kept_group_reads_no_store()
    {
        var stores = PlantAndOffice();

        Assert.Empty(stores.StoresOf(new Person("dave", null)));
        Assert.Empty(stores.StoresOf(new Person("erin", null) { Groups = ["g-unknown"] }));
    }

    [Fact]
    public void Stores_are_named_under_the_base_iri_and_none_configured_names_nothing()
    {
        Assert.Equal("https://example.org/ontology/Item/I-1", PlantAndOffice().Iris!.For("Item", "I-1"));
        Assert.Null(new ConfiguredGroupStores(Options.Create(new MaterializationOptions()), new Clients()).Iris);
    }

    [Fact]
    public void A_group_without_a_store_address_or_stores_without_a_base_iri_do_not_start()
    {
        Assert.Throws<InvalidOperationException>(() => Stores(o => o.Groups["g-plant"] = new GroupStoreOptions()));
        Assert.Throws<InvalidOperationException>(() => Stores(o =>
        {
            o.BaseIri = "";
            o.Groups["g-plant"] = new GroupStoreOptions { Url = "http://plant.test" };
        }));
    }
}
