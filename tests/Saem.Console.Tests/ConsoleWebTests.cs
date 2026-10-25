using System.Net;
using Xunit;

namespace Saem.Console.Tests;

/// <summary>Every answer the console gives carries the headers that keep its page from running another site's script or being framed.</summary>
public sealed class ConsoleWebTests
{
    [Fact]
    public async Task Answers_carry_a_content_security_policy_of_the_consoles_own_origin()
    {
        await using var server = await ConsoleServer.StartAsync();
        using var browser = server.Client();

        using var response = await browser.GetAsync("api/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }
}
