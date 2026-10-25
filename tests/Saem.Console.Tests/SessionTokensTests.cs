using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Saem.Console.Tests;

/// <summary>
/// saem `c838-01`: a browser session outlives its access token by renewing it with the refresh token — once, shared by
/// the requests that arrive together — and ends when it cannot be renewed.
/// </summary>
public sealed class SessionTokensTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_token_far_from_expiry_is_left_as_it_is()
    {
        var issuer = new Issuer();
        var context = Context(expiresIn: TimeSpan.FromMinutes(10));

        await Tokens(issuer).ValidateAsync(context);

        Assert.Equal(0, issuer.Requests);
        Assert.False(context.ShouldRenew);
        Assert.NotNull(context.Principal);
    }

    [Fact]
    public async Task A_token_about_to_expire_is_renewed_and_the_session_carries_the_new_tokens()
    {
        var issuer = new Issuer();
        var context = Context(expiresIn: TimeSpan.FromSeconds(30));

        await Tokens(issuer).ValidateAsync(context);

        Assert.Equal(["grant_type=refresh_token&refresh_token=refresh-1"], issuer.Forms);
        Assert.True(context.ShouldRenew);
        Assert.Equal("access-2", context.Properties.GetTokenValue("access_token"));
        Assert.Equal("refresh-2", context.Properties.GetTokenValue("refresh_token"));
        Assert.Equal(Now.AddMinutes(5), DateTimeOffset.Parse(context.Properties.GetTokenValue("expires_at")!, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Requests_that_arrive_together_spend_the_refresh_token_once()
    {
        var issuer = new Issuer();
        var tokens = Tokens(issuer);
        var first = Context(expiresIn: TimeSpan.Zero);
        var second = Context(expiresIn: TimeSpan.Zero);

        await Task.WhenAll(tokens.ValidateAsync(first), tokens.ValidateAsync(second));

        Assert.Equal(1, issuer.Requests);
        Assert.Equal("access-2", second.Properties.GetTokenValue("access_token"));
    }

    [Fact]
    public async Task A_refused_renewal_or_no_refresh_token_ends_the_session()
    {
        var refused = Context(expiresIn: TimeSpan.Zero);
        var without = Context(expiresIn: TimeSpan.Zero, refreshToken: null);

        await Tokens(new Issuer(refuse: true)).ValidateAsync(refused);
        await Tokens(new Issuer()).ValidateAsync(without);

        Assert.Null(refused.Principal);
        Assert.Null(without.Principal);
    }

    private static SessionTokens Tokens(Issuer issuer) => new(TestIssuer.For(issuer, clientSecret: "s"), new FixedTime(Now));

    private static CookieValidatePrincipalContext Context(TimeSpan expiresIn, string? refreshToken = "refresh-1")
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton<IAuthenticationService, NoAuthentication>().BuildServiceProvider() };
        var properties = new AuthenticationProperties();
        var stored = new List<AuthenticationToken>
        {
            new() { Name = "access_token", Value = "access-1" },
            new() { Name = "expires_at", Value = Now.Add(expiresIn).ToString("o", System.Globalization.CultureInfo.InvariantCulture) },
        };
        if (refreshToken is not null)
        {
            stored.Add(new() { Name = "refresh_token", Value = refreshToken });
        }

        properties.StoreTokens(stored);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "pat")], "test")), properties, CookieAuthenticationDefaults.AuthenticationScheme);
        return new CookieValidatePrincipalContext(http, new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler)), new CookieAuthenticationOptions(), ticket);
    }

    private sealed class Issuer(bool refuse = false) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => _requests;

        public List<string> Forms { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (Forms)
            {
                Forms.Add(form);
            }

            await Task.Delay(50, cancellationToken);
            return refuse
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant"}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"access-2","refresh_token":"refresh-2","expires_in":300,"token_type":"Bearer"}""") };
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class NoAuthentication : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }
}
