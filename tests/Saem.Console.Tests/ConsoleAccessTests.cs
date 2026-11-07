using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
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

    /// <summary>
    /// "No token is unauthorized" held to the routes as the host maps them, not to the one named above: a route that
    /// answers a caller presenting nothing is an unlisted member of the public surface. The two a browser needs before
    /// it has a session — sign in and sign out — are the only routes that are not behind a token.
    /// </summary>
    [Fact]
    public async Task Every_mapped_route_but_sign_in_and_sign_out_is_unauthorized_without_a_token()
    {
        using var anonymous = _server.Client();
        var answered = new List<string>();
        var probed = 0;
        foreach (var endpoint in _server.Endpoints)
        {
            var path = string.Concat(endpoint.RoutePattern.PathSegments.Select(segment =>
                "/" + string.Concat(segment.Parts.Select(part => part switch
                {
                    RoutePatternLiteralPart literal => literal.Content,
                    RoutePatternParameterPart => "probe",
                    _ => string.Empty,
                }))));
            if (path is "/login" or "/logout")
            {
                continue;
            }

            foreach (var method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path.TrimStart('/'));
                if (method is "POST" or "PUT" or "PATCH")
                {
                    // The console's own header, so the request is held by the token and not by the browser guard ahead of it.
                    request.Headers.Add(ConsoleAuthentication.RequestHeader, "1");
                    request.Content = JsonContent.Create(new { });
                }

                var response = await anonymous.SendAsync(request, TestContext.Current.CancellationToken);
                probed++;
                if (response.StatusCode != HttpStatusCode.Unauthorized)
                {
                    answered.Add($"{method} {endpoint.RoutePattern.RawText} -> {(int)response.StatusCode}");
                }
            }
        }

        Assert.True(probed >= 10, $"the console maps more than a handful of route and method pairs; a walk that found {probed} is not walking the host");
        Assert.Empty(answered);
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
