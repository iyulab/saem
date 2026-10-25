using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Saem.Console;

/// <summary>The issuer the console trusts and the audience a token must name to be accepted here.</summary>
public sealed class ConsoleIdentityOptions
{
    /// <summary>The issuer, as its tokens' <c>iss</c> names it; its discovery document is read from here.</summary>
    public string Authority { get; set; } = "";

    /// <summary>The console's own audience — not the MCP server's: a token meant for one is never accepted by the other.</summary>
    public string Audience { get; set; } = "";

    /// <summary>Whether the issuer must be reached over HTTPS. Off only for a local development issuer.</summary>
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
/// Wires the console: bearer tokens from the configured issuer, for the console's own audience, and the person's roles
/// from the groups the token names. Configuration lives under <c>Saem:Console</c>: <c>Identity</c>
/// (<see cref="ConsoleIdentityOptions"/>) and <c>Roles</c> (<see cref="ConsoleRoleOptions"/>). Nothing here names a source
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
            .Validate(i => !string.IsNullOrEmpty(i.Authority) && !string.IsNullOrEmpty(i.Audience), "Saem:Console:Identity names the trusted issuer (Authority) and the console's audience (Audience).")
            .ValidateOnStart();
        services.AddOptions<ConsoleRoleOptions>()
            .Bind(console.GetSection("Roles"))
            .Validate(r => r.Builders.Count > 0, "Saem:Console:Roles:Builders names at least one group; a console no one may use is a mistake, not a lock.")
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
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

    /// <summary>Maps the console's API under <c>/api</c>.</summary>
    public static WebApplication MapSaemConsole(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseAuthentication();
        app.UseAuthorization();

        var api = app.MapGroup("/api").RequireAuthorization(ConsoleRoles.AnyRole);
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

/// <summary>Adds a role claim for each console role the token's groups hold; a group the mapping does not name holds none.</summary>
internal sealed class GroupRoles(IOptions<ConsoleRoleOptions> roles) : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity || identity.HasClaim(c => c.Type == ClaimTypes.Role))
        {
            return Task.FromResult(principal);
        }

        var groups = principal.FindAll("groups").Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var (role, holders) in new[] { (ConsoleRoles.Builder, roles.Value.Builders), (ConsoleRoles.Auditor, roles.Value.Auditors) })
        {
            if (holders.Any(groups.Contains))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
            }
        }

        return Task.FromResult(principal);
    }
}
