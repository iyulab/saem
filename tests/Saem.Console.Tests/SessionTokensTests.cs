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

    /// <summary>
    /// An issuer that gives no answer (a 5xx, a refused connection) has not refused the session: while the access token the
    /// session holds still lives, the session goes on and the renewal is tried again later. Only an issuer that says no ends it.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(null, true)]
    public async Task An_issuer_that_gives_no_answer_does_not_end_a_session_whose_token_still_lives(HttpStatusCode? status, bool disconnect)
    {
        var context = Context(expiresIn: TimeSpan.FromSeconds(30));

        await Tokens(new Issuer(status: status, disconnect: disconnect)).ValidateAsync(context);

        Assert.NotNull(context.Principal);
        Assert.False(context.ShouldRenew);
        Assert.Equal("access-1", context.Properties.GetTokenValue("access_token"));
        Assert.Equal("refresh-1", context.Properties.GetTokenValue("refresh_token"));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, false)]
    [InlineData(null, true)]
    public async Task An_issuer_that_gives_no_answer_cannot_keep_a_session_whose_token_has_expired(HttpStatusCode? status, bool disconnect)
    {
        var context = Context(expiresIn: TimeSpan.FromSeconds(-1));

        await Tokens(new Issuer(status: status, disconnect: disconnect)).ValidateAsync(context);

        Assert.Null(context.Principal);
    }

    [Fact]
    public async Task A_renewal_that_got_no_answer_is_not_asked_again_by_every_request_of_the_next_minute()
    {
        var issuer = new Issuer(status: HttpStatusCode.ServiceUnavailable);
        var tokens = Tokens(issuer);

        await tokens.ValidateAsync(Context(expiresIn: TimeSpan.FromSeconds(30)));
        await tokens.ValidateAsync(Context(expiresIn: TimeSpan.FromSeconds(30)));

        Assert.Equal(1, issuer.Requests);
    }

    /// <summary>
    /// A session's roles follow the groups the issuer names now, not only the ones it named at sign-in (OpenID Connect Core §12.2:
    /// the ID token a renewal returns, if it returns one): a person taken out of a group at the issuer loses its role the next time
    /// the session renews, rather than keeping it until the cookie ends.
    /// </summary>
    [Fact]
    public async Task A_renewals_id_token_replaces_the_groups_the_session_carries()
    {
        var context = Context(expiresIn: TimeSpan.FromSeconds(30), groups: ["ontology-builders", "plant"]);

        await Tokens(new Issuer(idToken: IdToken(groups: ["plant", "auditors"]))).ValidateAsync(context);

        Assert.Equal(["auditors", "plant"], context.Principal!.FindAll("groups").Select(c => c.Value).Order(StringComparer.Ordinal));
        Assert.True(context.ShouldRenew);
        Assert.Equal("pat", context.Principal!.FindFirst("sub")!.Value);
    }

    [Fact]
    public async Task A_renewals_id_token_that_names_no_group_takes_every_group_from_the_session()
    {
        var context = Context(expiresIn: TimeSpan.FromSeconds(30), groups: ["ontology-builders"]);

        await Tokens(new Issuer(idToken: IdToken(groups: []))).ValidateAsync(context);

        Assert.NotNull(context.Principal);
        Assert.Empty(context.Principal.FindAll("groups"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_renewal_that_says_nothing_of_groups_leaves_the_session_its_groups(bool withIdToken)
    {
        var context = Context(expiresIn: TimeSpan.FromSeconds(30), groups: ["ontology-builders"]);

        await Tokens(new Issuer(idToken: withIdToken ? IdToken(groups: null) : null)).ValidateAsync(context);

        Assert.Equal(["ontology-builders"], context.Principal!.FindAll("groups").Select(c => c.Value));
    }

    /// <summary>An ID token that is not this session's person's, or not for this console at this issuer, is no renewal.</summary>
    [Theory]
    [InlineData("someone-else", "https://issuer.test", "saem-console")]
    [InlineData("pat", "https://elsewhere.test", "saem-console")]
    [InlineData("pat", "https://issuer.test", "another-client")]
    public async Task A_renewals_id_token_that_is_not_for_this_session_ends_it(string subject, string issuer, string audience)
    {
        var context = Context(expiresIn: TimeSpan.FromSeconds(30), groups: ["ontology-builders"]);

        await Tokens(new Issuer(idToken: IdToken(groups: ["ontology-builders"], subject: subject, issuer: issuer, audience: audience))).ValidateAsync(context);

        Assert.Null(context.Principal);
    }

    [Fact]
    public async Task A_renewals_id_token_that_cannot_be_read_ends_the_session()
    {
        var context = Context(expiresIn: TimeSpan.FromSeconds(30), groups: ["ontology-builders"]);

        await Tokens(new Issuer(idToken: "not-a-token")).ValidateAsync(context);

        Assert.Null(context.Principal);
    }

    /// <summary>An ID token as a token endpoint returns it to the client directly: the claims are read, the signature is not checked (OpenID Connect Core §3.1.3.7 — TLS to the endpoint vouches).</summary>
    private static string IdToken(string[]? groups, string subject = "pat", string issuer = TestIssuer.IssuerName, string audience = "saem-console")
    {
        var claims = new Dictionary<string, object> { ["iss"] = issuer, ["aud"] = audience, ["sub"] = subject };
        if (groups is not null)
        {
            claims["groups"] = groups;
        }

        static string Encode(object value) => Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(System.Text.Json.JsonSerializer.Serialize(value));
        return $"{Encode(new { alg = "none" })}.{Encode(claims)}.";
    }

    private static SessionTokens Tokens(Issuer issuer) => new(TestIssuer.For(issuer, clientSecret: "s"), new FixedTime(Now));

    private static CookieValidatePrincipalContext Context(TimeSpan expiresIn, string? refreshToken = "refresh-1", string[]? groups = null)
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
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "pat"), .. (groups ?? []).Select(g => new Claim("groups", g))], "test")), properties, CookieAuthenticationDefaults.AuthenticationScheme);
        return new CookieValidatePrincipalContext(http, new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler)), new CookieAuthenticationOptions(), ticket);
    }

    private sealed class Issuer(bool refuse = false, HttpStatusCode? status = null, bool disconnect = false, string? idToken = null) : HttpMessageHandler
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
            if (disconnect)
            {
                throw new HttpRequestException("Connection refused.");
            }

            if (status is { } failing)
            {
                return new HttpResponseMessage(failing) { Content = new StringContent("upstream trouble") };
            }

            return refuse
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant"}""") }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                    idToken is null
                        ? """{"access_token":"access-2","refresh_token":"refresh-2","expires_in":300,"token_type":"Bearer"}"""
                        : $$"""{"access_token":"access-2","refresh_token":"refresh-2","expires_in":300,"token_type":"Bearer","id_token":"{{idToken}}"}""")
                };
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
