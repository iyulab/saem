using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Saem.Console.Tests.Live;

/// <summary>
/// The console against the rig's issuer (<c>SAEM_RIG_ISSUER</c>): a person's token for the console's audience is
/// accepted, their group maps to a role, and a person in another group is turned away.
/// </summary>
public sealed class ConsoleRigLiveTests : IAsyncLifetime
{
    private static readonly string Issuer = Environment.GetEnvironmentVariable("SAEM_RIG_ISSUER")
        ?? throw new InvalidOperationException("SAEM_RIG_ISSUER names the rig's issuer (tests/rig/compose.password-login.yaml).");

    private ConsoleServer _server = null!;

    public async ValueTask InitializeAsync() => _server = await ConsoleServer.StartAsync(builders: "g-plant", liveIssuer: Issuer);

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task A_rig_person_in_the_builders_group_is_a_builder_and_one_in_another_group_is_forbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        using var pat = _server.Client(await TokenAsync("pat", ConsoleServer.Audience, ct));
        using var olive = _server.Client(await TokenAsync("olive", ConsoleServer.Audience, ct));
        using var forMcp = _server.Client(await TokenAsync("pat", "saem", ct));

        var me = await pat.GetFromJsonAsync<Caller>("api/me", ct);
        Assert.Equal("pat", me!.Subject);
        Assert.Equal([ConsoleRoles.Builder], me.Roles);
        Assert.Equal(HttpStatusCode.Forbidden, (await olive.GetAsync("api/me", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await forMcp.GetAsync("api/me", ct)).StatusCode);
    }

    private static async Task<string> TokenAsync(string person, string audience, CancellationToken ct)
    {
        using var http = new HttpClient();
        using var response = await http.PostAsync(Issuer + "/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = person,
            ["client_secret"] = "rig-local-only",
            ["scope"] = "saem-rig",
            ["audience"] = audience,
        }), ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("access_token").GetString()!;
    }
}
