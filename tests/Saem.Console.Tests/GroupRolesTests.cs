using System.Security.Claims;
using Microsoft.Extensions.Options;
using Xunit;

namespace Saem.Console.Tests;

/// <summary>
/// The console roles a caller holds come from the groups their token carries and from nothing else, and mapping them never
/// changes the principal it was handed: the transformation runs more than once per request, each time from the token's claims.
/// </summary>
public sealed class GroupRolesTests
{
    private static GroupRoles Mapping() => new(Options.Create(new ConsoleRoleOptions { Builders = ["keepers"], Auditors = ["watchers"] }));

    private static ClaimsPrincipal Caller(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test", "sub", ClaimsIdentity.DefaultRoleClaimType));

    private static string[] RolesOf(ClaimsPrincipal principal) => [.. principal.FindAll(ClaimsIdentity.DefaultRoleClaimType).Select(c => c.Value).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task A_role_the_token_carries_itself_is_dropped_and_the_roles_come_from_its_groups()
    {
        var caller = Caller(new Claim("sub", "alice"), new Claim(ClaimsIdentity.DefaultRoleClaimType, ConsoleRoles.Builder), new Claim("groups", "watchers"));

        var mapped = await Mapping().TransformAsync(caller);

        Assert.Equal([ConsoleRoles.Auditor], RolesOf(mapped));
    }

    [Fact]
    public async Task A_group_the_mapping_does_not_name_holds_no_role()
    {
        var mapped = await Mapping().TransformAsync(Caller(new Claim("sub", "bob"), new Claim("groups", "everyone")));

        Assert.Empty(RolesOf(mapped));
    }

    [Fact]
    public async Task The_principal_that_was_handed_in_is_left_as_it_was_and_mapping_it_again_gives_the_same_roles()
    {
        var caller = Caller(new Claim("sub", "alice"), new Claim(ClaimsIdentity.DefaultRoleClaimType, "carried"), new Claim("groups", "keepers"));

        var first = await Mapping().TransformAsync(caller);
        var second = await Mapping().TransformAsync(caller);

        Assert.Equal(["carried"], RolesOf(caller));
        Assert.Equal([ConsoleRoles.Builder], RolesOf(first));
        Assert.Equal(RolesOf(first), RolesOf(second));
    }
}
