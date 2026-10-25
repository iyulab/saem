using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Saem.Ontology;

namespace Saem.Console;

/// <summary>
/// The issuer the console trusts, the audience a token must name to be accepted here, and the console's own client at
/// that issuer — the confidential client that signs people in and exchanges their tokens.
/// </summary>
public sealed class ConsoleIdentityOptions
{
    /// <summary>The issuer, as its tokens' <c>iss</c> names it; its discovery document is read from here.</summary>
    public string Authority { get; set; } = "";

    /// <summary>The console's own audience — not the MCP server's: a token meant for one is never accepted by the other.</summary>
    public string Audience { get; set; } = "";

    /// <summary>The console's client id at the issuer.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>The console's client secret at the issuer.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>The scopes asked for at sign-in besides <c>openid</c>.</summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>
    /// Whether the issuer must be reached over HTTPS — and the console's session cookie sent over HTTPS only. Off only
    /// for a local development issuer and console.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;
}

/// <summary>
/// Which of the identity provider's groups hold which console role (saem D-88 C3). saem keeps the mapping and nothing
/// else about a person (D-31): who belongs to a group is the provider's to say.
/// </summary>
public sealed class ConsoleRoleOptions
{
    /// <summary>The groups whose members keep the ontology: read and, later, change the declarations.</summary>
    public List<string> Builders { get; set; } = [];

    /// <summary>The groups whose members may read who read what. Apart from the builders, since that record is personal data.</summary>
    public List<string> Auditors { get; set; } = [];
}

/// <summary>The console's roles, as the API names them.</summary>
public static class ConsoleRoles
{
    /// <summary>Keeps the ontology.</summary>
    public const string Builder = "builder";

    /// <summary>Reads who read what.</summary>
    public const string Auditor = "auditor";

    /// <summary>The policy every console endpoint requires: one role or the other.</summary>
    public const string AnyRole = "console";
}

/// <summary>
/// How a request reaches the console (saem `c838-01`, backend-for-frontend): a browser holds a session cookie and never
/// a token — the console signs the person in with the authorization code flow as a confidential client and keeps their
/// tokens in the session — while a program presents a bearer token for the console's audience.
/// </summary>
public static class ConsoleAuthentication
{
    /// <summary>The scheme every endpoint authenticates with: the bearer scheme when a bearer token is presented, else the session.</summary>
    public const string Scheme = "saem-console";

    /// <summary>
    /// The header a browser's state-changing request carries. A cross-origin page cannot add it without a preflight the
    /// console never approves, so a forged form post or fetch from another site is refused (CSRF).
    /// </summary>
    public const string RequestHeader = "X-Saem-Console";
}

/// <summary>
/// Wires the console: a browser session signed in at the configured issuer or a bearer token for the console's own
/// audience, and the person's roles from the groups the token names. Configuration lives under <c>Saem:Console</c>: <c>Ontology</c> (the catalog file),
/// <c>Identity</c> (<see cref="ConsoleIdentityOptions"/>) and <c>Roles</c> (<see cref="ConsoleRoleOptions"/>). Nothing here names a source
/// or a store: the console reads neither (saem D-89).
/// </summary>
public static class SaemConsole
{
    /// <summary>Adds the console's services, read from <paramref name="configuration"/>.</summary>
    public static IServiceCollection AddSaemConsole(this IServiceCollection services, IConfiguration configuration)
    {
        var console = configuration.GetSection("Saem:Console");
        services.AddOptions<ConsoleIdentityOptions>()
            .Bind(console.GetSection("Identity"))
            .Validate(
                i => !string.IsNullOrEmpty(i.Authority) && !string.IsNullOrEmpty(i.Audience) && !string.IsNullOrEmpty(i.ClientId),
                "Saem:Console:Identity names the trusted issuer (Authority), the console's audience (Audience) and its client there (ClientId, ClientSecret).")
            .ValidateOnStart();
        services.AddOptions<ConsoleCatalogOptions>()
            .Bind(console)
            .Validate(c => !string.IsNullOrEmpty(c.Ontology), "Saem:Console:Ontology names the catalog file the console shows.")
            .ValidateOnStart();
        services.AddSingleton<IOntologyCatalog>(provider => new FileOntologyCatalog(provider.GetRequiredService<IOptions<ConsoleCatalogOptions>>().Value.Ontology));
        services.Configure<ConsoleLookupOptions>(console.GetSection("Lookup"));
        services.Configure<ConsoleCheckOptions>(console);
        services.AddHttpClient(nameof(ConsoleIssuer));
        services.AddSingleton<ConsoleIssuer>();
        services.AddSingleton<IConsoleLookups, McpConsoleLookups>();
        services.AddOptions<ConsoleRoleOptions>()
            .Bind(console.GetSection("Roles"))
            .Validate(r => r.Builders.Count > 0, "Saem:Console:Roles:Builders names at least one group; a console no one may use is a mistake, not a lock.")
            .ValidateOnStart();

        // The session cookie carries the session, sealed with the data-protection key ring: instances that are to read one
        // another's cookies share the ring (a directory they all reach) and the application name, which by default is the
        // install path — so it is fixed here, not left to differ from one host to the next.
        var keyRing = services.AddDataProtection().SetApplicationName(console["DataProtection:ApplicationName"] is { Length: > 0 } name ? name : "saem-console");
        if (console["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
        {
            keyRing.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        }

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = ConsoleAuthentication.Scheme;
                options.DefaultChallengeScheme = ConsoleAuthentication.Scheme;
            })
            .AddPolicyScheme(ConsoleAuthentication.Scheme, null, policy => policy.ForwardDefaultSelector = http =>
                PresentedBearer(http.Request) is not null ? JwtBearerDefaults.AuthenticationScheme : CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie()
            .AddOpenIdConnect()
            .AddJwtBearer();
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<IOptions<ConsoleIdentityOptions>>((cookie, identity) =>
            {
                // A session cookie as the IETF browser-based apps BCP has a BFF's: Secure, HttpOnly, SameSite=Strict, path /.
                var secure = identity.Value.RequireHttpsMetadata;
                cookie.Cookie.Name = secure ? "__Host-saem-console" : "saem-console";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.Cookie.SecurePolicy = secure ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                cookie.Cookie.Path = "/";
                cookie.Events.OnRedirectToLogin = context => Status(context.Response, StatusCodes.Status401Unauthorized);
                cookie.Events.OnRedirectToAccessDenied = context => Status(context.Response, StatusCodes.Status403Forbidden);
            });
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SessionTokens>();
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<SessionTokens>((cookie, tokens) => cookie.Events.OnValidatePrincipal = tokens.ValidateAsync);
        services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme)
            .Configure<IOptions<ConsoleIdentityOptions>>((oidc, identity) =>
            {
                var id = identity.Value;
                oidc.Authority = id.Authority;
                oidc.ClientId = id.ClientId;
                oidc.ClientSecret = id.ClientSecret;
                oidc.RequireHttpsMetadata = id.RequireHttpsMetadata;
                oidc.ResponseType = "code";
                oidc.UsePkce = true;
                // The code comes back on a top-level GET, so the handler's correlation and nonce cookies need only Lax.
                oidc.ResponseMode = "query";
                oidc.CorrelationCookie.SameSite = SameSiteMode.Lax;
                oidc.NonceCookie.SameSite = SameSiteMode.Lax;
                oidc.CorrelationCookie.SecurePolicy = oidc.NonceCookie.SecurePolicy = id.RequireHttpsMetadata ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                oidc.SaveTokens = true;
                oidc.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                oidc.MapInboundClaims = false;
                oidc.GetClaimsFromUserInfoEndpoint = false;
                oidc.TokenValidationParameters.NameClaimType = "sub";
                oidc.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
                oidc.Scope.Clear();
                oidc.Scope.Add("openid");
                foreach (var scope in id.Scopes)
                {
                    oidc.Scope.Add(scope);
                }
            });
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<ConsoleIdentityOptions>>((jwt, identity) =>
            {
                var id = identity.Value;
                jwt.Authority = id.Authority;
                jwt.Audience = id.Audience;
                jwt.RequireHttpsMetadata = id.RequireHttpsMetadata;
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters.NameClaimType = "sub";
                jwt.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
                jwt.TokenValidationParameters.ValidateAudience = true;
            });
        services.AddTransient<IClaimsTransformation, GroupRoles>();
        services.AddAuthorizationBuilder()
            .AddPolicy(ConsoleRoles.AnyRole, policy => policy.RequireAuthenticatedUser().RequireRole(ConsoleRoles.Builder, ConsoleRoles.Auditor))
            .AddPolicy(ConsoleRoles.Builder, policy => policy.RequireAuthenticatedUser().RequireRole(ConsoleRoles.Builder));
        return services;
    }

    /// <summary>The bearer token the request presents, or <see langword="null"/> when it presents none.</summary>
    internal static string? PresentedBearer(HttpRequest request) =>
        request.Headers.Authorization.ToString() is { } header && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;

    /// <summary>The token the person reached the console with: the presented bearer token, or the one their session keeps.</summary>
    internal static async Task<string?> PersonsTokenAsync(HttpContext http) =>
        PresentedBearer(http.Request) ?? await http.GetTokenAsync(CookieAuthenticationDefaults.AuthenticationScheme, "access_token");

    private static Task Status(HttpResponse response, int status)
    {
        response.StatusCode = status;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Serves the web UI, and maps the console's sign-in and sign-out under <c>/login</c> and <c>/logout</c> and its API
    /// under <c>/api</c>.
    /// </summary>
    public static WebApplication MapSaemConsole(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(async (http, next) =>
        {
            // The browser's session is only as safe as the page is from injected script: everything here comes from the
            // console's own origin, and no other site may frame it.
            var headers = http.Response.Headers;
            // The one inline style allowed is the graph library's own, by its hash: the rule it adds to its container.
            headers.ContentSecurityPolicy = "default-src 'self'; style-src 'self' 'sha256-pgvDUBa4IjFA2yuSJ2cqcyxmNYJMborsd0ORcRv9vw8='; "
                + "base-uri 'none'; form-action 'self'; frame-ancestors 'none'; object-src 'none'";
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            await next(http);
        });

        // The web UI (web/, built into wwwroot): a page that holds no data — what it shows it asks the API for.
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseAuthentication();
        app.Use(async (http, next) =>
        {
            // A browser's state-changing request must carry the console's header (CSRF); a program's bearer request needs none.
            if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && !HttpMethods.IsOptions(http.Request.Method)
                && PresentedBearer(http.Request) is null && !http.Request.Headers.ContainsKey(ConsoleAuthentication.RequestHeader))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            await next(http);
        });
        app.UseAuthorization();

        // Sign-in sends the browser to the issuer and back (authorization code flow with PKCE); only a local return
        // address is followed, so the console is never an open redirect.
        app.MapGet("/login", (string? returnUrl) => Results.Challenge(
            new AuthenticationProperties { RedirectUri = returnUrl is { Length: > 0 } && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal) ? returnUrl : "/" },
            [OpenIdConnectDefaults.AuthenticationScheme]));
        app.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        var api = app.MapGroup("/api").RequireAuthorization(ConsoleRoles.AnyRole);
        CatalogApi.Map(api);
        OverviewApi.Map(api);

        // A local graph is a lookup asked as the person looking: the answer, partition reports and refusals included, as
        // the MCP server gives it to that person — the console adds nothing and reads no source (saem D-89 ①).
        api.MapPost("/graph/local", async (HttpContext http, IConsoleLookups lookups, JsonElement call, CancellationToken ct) =>
        {
            var presented = await PersonsTokenAsync(http) ?? "";
            try
            {
                return Results.Json(await lookups.LookupAsync(presented, call, ct));
            }
            catch (LookupUnavailableException e)
            {
                return TypedResults.Problem(title: "The lookup could not be asked.", detail: e.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        }).RequireAuthorization(ConsoleRoles.Builder);
        api.MapGet("/me", (ClaimsPrincipal user) => TypedResults.Ok(new Caller(
            user.FindFirstValue("sub") ?? "",
            [.. user.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)])));
        return app;
    }
}

/// <summary>Who the console takes the caller to be.</summary>
/// <param name="Subject">The person, as the token's <c>sub</c> names them.</param>
/// <param name="Roles">The console roles their groups hold.</param>
public sealed record Caller(string Subject, IReadOnlyList<string> Roles);

/// <summary>
/// Gives the caller the console roles their token's groups hold — and only those: a role claim the token carries itself
/// is dropped, so no issuer setting can grant a console role the mapping does not. A group the mapping does not name
/// holds none.
/// </summary>
internal sealed class GroupRoles(IOptions<ConsoleRoleOptions> roles) : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true })
        {
            return Task.FromResult(principal);
        }

        // The principal may be transformed more than once per request: each time from the token's own claims, on a copy.
        var mapped = principal.Clone();
        var identity = (ClaimsIdentity)mapped.Identity!;
        foreach (var carried in identity.FindAll(identity.RoleClaimType).ToList())
        {
            identity.RemoveClaim(carried);
        }

        var groups = mapped.FindAll("groups").Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var (role, holders) in new[] { (ConsoleRoles.Builder, roles.Value.Builders), (ConsoleRoles.Auditor, roles.Value.Auditors) })
        {
            if (holders.Any(groups.Contains))
            {
                identity.AddClaim(new Claim(identity.RoleClaimType, role));
            }
        }

        return Task.FromResult(mapped);
    }
}
