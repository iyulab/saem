using System.Net;
using Xunit;

namespace Saem.Mcp.Tests;

/// <summary>A container orchestrator asks the host whether it is up: no token, and nothing in the answer but that.</summary>
public sealed class HealthProbeTests
{
    private const string OntologyJson = "[]";

    [Fact]
    public async Task The_probe_answers_without_a_token_and_the_endpoint_still_needs_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await TestServer.StartAsync(OntologyJson, new FakeLogins(new()));
        using var http = new HttpClient();

        using var health = await http.GetAsync(new Uri(server.Endpoint, SaemMcpServer.HealthPath), ct);
        using var mcp = await http.PostAsync(server.Endpoint, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), ct);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("", await health.Content.ReadAsStringAsync(ct));
        Assert.Equal(HttpStatusCode.Unauthorized, mcp.StatusCode);
    }
}
