using System.Net;
using System.Text;
using System.Web;
using Xunit;

namespace Saem.Console.Tests;

/// <summary>
/// saem `c838-01`: the browser signs in through the console — the authorization code flow with PKCE, the console as a
/// confidential client — and holds a session cookie, never a token; a browser's state-changing request carries the
/// console's header, so a forged cross-site request is refused.
/// </summary>
public sealed class ConsoleSessionTests : IAsyncLifetime
{
    private ConsoleServer _server = null!;

    public async ValueTask InitializeAsync() => _server = await ConsoleServer.StartAsync();

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Sign_in_sends_the_browser_to_the_issuer_for_a_code_with_pkce_and_hands_it_no_token()
    {
        using var browser = _server.Client(handler: new HttpClientHandler { AllowAutoRedirect = false });

        using var response = await browser.GetAsync("login?returnUrl=/catalog", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var to = response.Headers.Location!;
        Assert.StartsWith(ConsoleServer.Issuer + "/authorize", to.GetLeftPart(UriPartial.Path), StringComparison.Ordinal);
        var query = HttpUtility.ParseQueryString(to.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("saem-console", query["client_id"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrEmpty(query["code_challenge"]));
        Assert.Contains("openid", query["scope"]!.Split(' '));
        Assert.DoesNotContain("token", query["response_type"]!.Split(' '));
    }

    [Fact]
    public async Task An_api_call_without_a_session_is_unauthorized_rather_than_sent_to_sign_in()
    {
        using var browser = _server.Client(handler: new HttpClientHandler { AllowAutoRedirect = false });

        using var response = await browser.GetAsync("api/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_browsers_state_changing_request_without_the_consoles_header_is_refused_and_a_programs_bearer_request_needs_none()
    {
        using var browser = _server.Client();
        using var program = _server.Client(ConsoleServer.Token("alice", ["ontology-builders"]));
        using var forged = new StringContent("{}", Encoding.UTF8, "text/plain");

        using var refused = await browser.PostAsync("logout", forged, TestContext.Current.CancellationToken);
        using var withHeader = new HttpRequestMessage(HttpMethod.Post, "logout") { Headers = { { ConsoleAuthentication.RequestHeader, "1" } } };
        using var signedOut = await browser.SendAsync(withHeader, TestContext.Current.CancellationToken);
        using var bearer = await program.PostAsync("logout", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, bearer.StatusCode);
    }
}
