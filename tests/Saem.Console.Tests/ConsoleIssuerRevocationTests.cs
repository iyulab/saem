using System.Net;
using System.Text;
using Xunit;

namespace Saem.Console.Tests;

/// <summary>
/// Signing out ends the session at the issuer too, as far as the console can: the refresh token the session held is revoked
/// (RFC 7009) so a copy of the session cookie cannot go on renewing it. Revocation is best effort — an issuer that offers
/// none, or does not answer, leaves signing out as it was.
/// </summary>
public sealed class ConsoleIssuerRevocationTests
{
    [Fact]
    public async Task A_refresh_token_is_revoked_at_the_issuers_revocation_endpoint_as_the_console_client()
    {
        var issuer = new Answers(HttpStatusCode.OK);

        var outcome = await TestIssuer.For(issuer, clientId: "saem-console", clientSecret: "s3cret", revocable: true).RevokeAsync("refresh-1", TestContext.Current.CancellationToken);

        Assert.Equal(RevocationOutcome.Revoked, outcome);
        var request = Assert.Single(issuer.Requests);
        Assert.Equal((HttpMethod.Post, TestIssuer.RevocationEndpoint), (request.Method, request.Uri));
        Assert.Equal("token=refresh-1&token_type_hint=refresh_token", request.Form);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("saem-console:s3cret")), request.Authorization);
    }

    [Fact]
    public async Task An_issuer_that_offers_no_revocation_endpoint_is_not_asked()
    {
        var issuer = new Answers(HttpStatusCode.OK);

        var outcome = await TestIssuer.For(issuer, revocable: false).RevokeAsync("refresh-1", TestContext.Current.CancellationToken);

        Assert.Equal(RevocationOutcome.NotOffered, outcome);
        Assert.Empty(issuer.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_revocation_the_issuer_does_not_accept_is_reported_and_throws_nothing(HttpStatusCode status)
    {
        var outcome = await TestIssuer.For(new Answers(status), revocable: true).RevokeAsync("refresh-1", TestContext.Current.CancellationToken);

        Assert.Equal(RevocationOutcome.Failed, outcome);
    }

    [Fact]
    public async Task An_issuer_that_cannot_be_reached_is_reported_and_throws_nothing()
    {
        var outcome = await TestIssuer.For(new Answers(disconnect: true), revocable: true).RevokeAsync("refresh-1", TestContext.Current.CancellationToken);

        Assert.Equal(RevocationOutcome.Failed, outcome);
    }

    private sealed class Answers(HttpStatusCode status = HttpStatusCode.OK, bool disconnect = false) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Uri, string Form, string? Authorization)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString(), await request.Content!.ReadAsStringAsync(cancellationToken), request.Headers.Authorization?.ToString()));
            return disconnect ? throw new HttpRequestException("connection refused") : new HttpResponseMessage(status);
        }
    }
}
