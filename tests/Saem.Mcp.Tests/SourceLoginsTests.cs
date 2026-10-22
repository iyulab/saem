using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Xunit;

namespace Saem.Mcp.Tests;

/// <summary>How a person's token is kept, exchanged and handed to a source that takes one.</summary>
public class SourceLoginsTests
{
    private sealed class Recorder(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            return answer(request, body);
        }
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private static readonly TokenExchangeOptions Exchange = new()
    {
        TokenEndpoint = "http://issuer.test/token",
        ClientId = "saem",
        ClientSecret = "s3cret",
    };

    [Fact]
    public void A_presented_token_never_prints()
    {
        var token = PresentedToken.FromAuthorization("Bearer eyJhbGciOi.payload.sig")!;

        Assert.Equal("eyJhbGciOi.payload.sig", token.Value);
        Assert.Equal("[token]", token.ToString());
        Assert.DoesNotContain("eyJ", $"{token}", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer ")]
    public void Without_a_bearer_token_there_is_none(string? header) =>
        Assert.Null(PresentedToken.FromAuthorization(header));

    [Fact]
    public async Task An_exchange_asks_the_issuer_for_the_audience_as_saem_with_the_persons_token_as_subject()
    {
        var recorder = new Recorder((_, _) => Json(new { access_token = "for-morphdb", issued_token_type = "urn:ietf:params:oauth:token-type:access_token" }));
        var exchange = new TokenExchange(new HttpClient(recorder), Exchange);

        var token = await exchange.ForAsync(new PresentedToken("alice-token"), "morphdb", TestContext.Current.CancellationToken);

        Assert.Equal("for-morphdb", token);
        var (request, body) = Assert.Single(recorder.Requests);
        Assert.Equal("http://issuer.test/token", request.RequestUri!.ToString());
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal("saem:s3cret", Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
        var form = body.Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        Assert.Equal("urn:ietf:params:oauth:grant-type:token-exchange", form["grant_type"]);
        Assert.Equal("alice-token", form["subject_token"]);
        Assert.Equal("urn:ietf:params:oauth:token-type:access_token", form["subject_token_type"]);
        Assert.Equal("morphdb", form["audience"]);
    }

    [Fact]
    public async Task A_refused_exchange_is_an_error_that_carries_the_issuers_reason_and_not_the_token()
    {
        var recorder = new Recorder((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"invalid_grant"}"""),
        });
        var exchange = new TokenExchange(new HttpClient(recorder), Exchange);

        var refused = await Assert.ThrowsAsync<TokenExchangeException>(
            async () => await exchange.ForAsync(new PresentedToken("alice-token"), "morphdb", TestContext.Current.CancellationToken));

        Assert.Contains("invalid_grant", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("alice-token", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_morphdb_source_is_read_with_a_token_exchanged_for_its_audience()
    {
        var recorder = new Recorder((request, _) => request.RequestUri!.Host == "issuer.test"
            ? Json(new { access_token = "exchanged-for-forms" })
            : Json(new { data = Array.Empty<object>(), pagination = new { hasNext = false } }));
        var options = new SourceOptions { TokenExchange = Exchange };
        options.MorphDb["forms"] = new MorphDbSourceOptions { Url = "http://forms.test", ProjectId = Guid.NewGuid(), Audience = "forms-aud" };
        var logins = new ConfiguredSourceLogins(Options.Create(options), new Clients(recorder));

        var sessions = logins.SessionsFor(new Person("alice", null), new PresentedToken("alice-token"), "lookup-1");
        await using (var session = await sessions.OpenAsync("forms", TestContext.Current.CancellationToken))
        {
            await session.ReadAsync("t", "k", "v", [], TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, recorder.Requests.Count);
        var (exchange, body) = recorder.Requests[0];
        Assert.Equal("issuer.test", exchange.RequestUri!.Host);
        Assert.Contains("audience=forms-aud", body, StringComparison.Ordinal);
        var read = recorder.Requests[1].Request;
        Assert.Equal("http://forms.test/api/data/t?filter=k%3Aeq%3Av&select=k&page=1&pageSize=1000", read.RequestUri!.ToString());
        Assert.Equal("exchanged-for-forms", read.Headers.Authorization!.Parameter);
    }

    [Fact]
    public void A_morphdb_source_without_an_audience_or_an_exchange_is_refused_at_start()
    {
        var noAudience = new SourceOptions { TokenExchange = Exchange };
        noAudience.MorphDb["forms"] = new MorphDbSourceOptions { Url = "http://forms.test", ProjectId = Guid.NewGuid() };
        Assert.Throws<InvalidOperationException>(() => new ConfiguredSourceLogins(Options.Create(noAudience), new Clients(new HttpClientHandler())));

        var noExchange = new SourceOptions();
        noExchange.MorphDb["forms"] = new MorphDbSourceOptions { Url = "http://forms.test", ProjectId = Guid.NewGuid(), Audience = "a" };
        Assert.Throws<InvalidOperationException>(() => new ConfiguredSourceLogins(Options.Create(noExchange), new Clients(new HttpClientHandler())));
    }

    [Fact]
    public void A_system_configured_as_two_sources_is_refused()
    {
        var twice = new SourceOptions { TokenExchange = Exchange };
        twice.Postgres["forms"] = "Host=forms;Database=postgres";
        twice.MorphDb["forms"] = new MorphDbSourceOptions { Url = "http://forms.test", ProjectId = Guid.NewGuid(), Audience = "a" };

        Assert.Throws<InvalidOperationException>(() => new ConfiguredSourceLogins(Options.Create(twice), new Clients(new HttpClientHandler())));
    }
}
