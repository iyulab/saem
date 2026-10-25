using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Saem.Console.Tests;

/// <summary>
/// saem D-88 C2 · C3: the console takes a person from the identity provider's token — for the console's own audience —
/// and their roles from the groups the token names; a person in neither mapped group is turned away.
/// </summary>
public sealed class ConsoleAccessTests : IAsyncLifetime
{
    private ConsoleServer _server = null!;

    public async ValueTask InitializeAsync() => _server = await ConsoleServer.StartAsync();

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task A_person_in_a_mapped_group_is_told_who_they_are_and_the_roles_their_groups_hold()
    {
        using var client = _server.Client(ConsoleServer.Token("alice", ["ontology-builders", "privacy-office", "sales"]));

        var me = await client.GetFromJsonAsync<Caller>("api/me", TestContext.Current.CancellationToken);

        Assert.Equal("alice", me!.Subject);
        Assert.Equal([ConsoleRoles.Auditor, ConsoleRoles.Builder], me.Roles);
    }

    [Fact]
    public async Task No_token_is_unauthorized_and_a_token_in_no_mapped_group_is_forbidden()
    {
        using var anonymous = _server.Client();
        using var outsider = _server.Client(ConsoleServer.Token("carol", ["sales"]));

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("api/me", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync("api/me", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task A_role_the_token_carries_itself_is_not_a_console_role()
    {
        using var client = _server.Client(ConsoleServer.Token(
            "mallory", ["sales"], more: new() { [System.Security.Claims.ClaimTypes.Role] = ConsoleRoles.Builder, ["role"] = ConsoleRoles.Builder }));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("api/me", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task A_token_meant_for_the_mcp_server_is_not_accepted_here()
    {
        using var client = _server.Client(ConsoleServer.Token("alice", ["ontology-builders"], audience: "saem"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/me", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task A_console_no_group_may_use_does_not_start()
    {
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => ConsoleServer.StartAsync(builders: null));

        Assert.Contains("Builders names at least one group", refused.Message, StringComparison.Ordinal);
    }
}
