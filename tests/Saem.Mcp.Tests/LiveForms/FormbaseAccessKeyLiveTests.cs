using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Saem.Mcp.Tests.LiveForms;

/// <summary>
/// SD-15 on the rig: a group's Formbase host keeps its edges and identities for that group alone, and the host
/// itself asks no one who is calling — so it answers only a caller that presents one of its access keys (a key
/// names a calling service, not a person). The rig starts every Formbase with a key, and what saem sends
/// (the server's reads, the executor's writes) is proven by every group suite passing against it.
/// </summary>
public sealed class FormbaseAccessKeyLiveTests : IAsyncLifetime
{
    private GroupStoreRig _store = null!;

    public async ValueTask InitializeAsync() => _store = await GroupStoreRig.StartAsync("g-plant", TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await _store.DisposeAsync();

    [Fact]
    public async Task A_group_host_answers_only_a_caller_that_presents_one_of_its_keys()
    {
        var ct = TestContext.Current.CancellationToken;
        using var anonymous = new HttpClient { BaseAddress = _store.Formbase.BaseAddress };

        var refused = await anonymous.GetAsync("formtypes/edges/documents?limit=1", ct);
        using var wrong = new HttpClient { BaseAddress = _store.Formbase.BaseAddress };
        wrong.DefaultRequestHeaders.Authorization = new("Bearer", "not-a-key-of-this-host-0123456789abcdef");
        var wrongKey = await wrong.GetAsync("formtypes/edges/documents?limit=1", ct);
        var keyed = await _store.Formbase.GetAsync("formtypes/edges/documents?limit=1", ct);
        var health = await anonymous.GetAsync("health", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("/problems/access-key-required", (await refused.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, wrongKey.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, keyed.StatusCode);
        Assert.True(health.IsSuccessStatusCode, "the health probe is the one request a host answers without a key");
    }
}
