using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore.Authentication;
using Saem.Ontology;
using Saem.Query;

namespace Saem.Mcp;

/// <summary>The issuer saem trusts and the audience a token must name.</summary>
public sealed class IdentityOptions
{
    /// <summary>The issuer, as its tokens' <c>iss</c> names it; its discovery document is read from here.</summary>
    public string Authority { get; set; } = "";

    /// <summary>The audience a token must carry to be accepted here.</summary>
    public string Audience { get; set; } = "";

    /// <summary>Whether the issuer must be reached over HTTPS. Off only for a local development issuer.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;
}

/// <summary>The lookup's budget (saem D-84): how far one step may spread, and how long a lookup may take.</summary>
public sealed class LookupOptions
{
    /// <summary>The most candidates one step may read; a step with more is refused as too broad, none of them read.</summary>
    public int MaxCandidatesPerStep { get; set; } = LookupWalker.DefaultMaxCandidatesPerStep;

    /// <summary>How long a whole lookup may take before it is stopped and reported as not answered.</summary>
    public TimeSpan Deadline { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Wires the MCP server: bearer-token authentication against the configured issuer, the one
/// lookup tool, and Streamable HTTP. Configuration lives under <c>Saem</c>: <c>Ontology</c> (the
/// declarations file), <c>Identity</c> (<see cref="IdentityOptions"/>), <c>Sources</c>
/// (<see cref="SourceOptions"/>), <c>Materialization</c> (<see cref="MaterializationOptions"/>), <c>Lookup</c>
/// (<see cref="LookupOptions"/>) and <c>Audit</c> (<see cref="AuditOptions"/>).
/// </summary>
public static class SaemMcpServer
{
    /// <summary>Adds the server's services, read from <paramref name="configuration"/>.</summary>
    public static IServiceCollection AddSaemMcp(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var saem = configuration.GetSection("Saem");
        // Without an issuer and an audience every request would be turned away; say so at start instead.
        services.AddOptions<IdentityOptions>()
            .Bind(saem.GetSection("Identity"))
            .Validate(i => !string.IsNullOrEmpty(i.Authority) && !string.IsNullOrEmpty(i.Audience), "Saem:Identity names the trusted issuer (Authority) and this server's audience (Audience).")
            .ValidateOnStart();
        services.Configure<SourceOptions>(saem.GetSection("Sources"));
        services.Configure<MaterializationOptions>(saem.GetSection("Materialization"));
        services.Configure<AuditOptions>(saem.GetSection("Audit"));
        services.AddOptions<LookupOptions>()
            .Bind(saem.GetSection("Lookup"))
            .Validate(l => l.MaxCandidatesPerStep >= 1 && l.Deadline > TimeSpan.Zero, "Saem:Lookup allows at least one candidate per step and a positive deadline.")
            .ValidateOnStart();
        services.TryAddSingleton<InteractionLog>();

        var ontologyPath = saem["Ontology"];
        services.TryAddSingleton<IOntologyCatalog>(_ => new FileOntologyCatalog(
            string.IsNullOrEmpty(ontologyPath) ? throw new InvalidOperationException("Saem:Ontology names the ontology declarations file.") : ontologyPath));
        services.AddHttpClient();
        services.AddHttpContextAccessor();
        services.TryAddSingleton<ISourceLogins, ConfiguredSourceLogins>();
        services.TryAddSingleton<IGroupStores, ConfiguredGroupStores>();
        services.TryAddSingleton<IProjectionStates, FormbaseProjectionStates>();
        services.AddSingleton<LookupTool>();
        services.AddHostedService<CatalogSourcesCheck>();

        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                // A request without a valid token is told where to find the issuer (RFC 9728).
                options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
            })
            .AddJwtBearer()
            .AddMcp();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<IdentityOptions>>((jwt, identity) =>
            {
                var id = identity.Value;
                jwt.Authority = id.Authority;
                jwt.Audience = id.Audience;
                jwt.RequireHttpsMetadata = id.RequireHttpsMetadata;
                // Claims keep their token names: the person is 'sub', the agent is in 'act'.
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters.NameClaimType = "sub";
                jwt.TokenValidationParameters.ValidateAudience = true;
            });
        services.AddOptions<McpAuthenticationOptions>(McpAuthenticationDefaults.AuthenticationScheme)
            .Configure<IOptions<IdentityOptions>>((mcp, identity) =>
            {
                mcp.ResourceMetadata = new ModelContextProtocol.Authentication.ProtectedResourceMetadata
                {
                    AuthorizationServers = { identity.Value.Authority },
                };
            });
        services.AddAuthorization();

        services.AddMcpServer()
            .WithHttpTransport(http => http.Stateless = true)
            .WithListToolsHandler((request, ct) => request.Services!.GetRequiredService<LookupTool>().ListAsync(ct))
            .WithCallToolHandler((request, ct) => request.Services!.GetRequiredService<LookupTool>()
                .CallAsync(request.Params!, Person.From(request.User), PresentedTokenOf(request.Services!), ct));
        return services;
    }

    // The token the request was authenticated with: authentication has already validated it, so it is
    // the person's own token, kept only to be exchanged for a source's.
    private static PresentedToken? PresentedTokenOf(IServiceProvider services) =>
        PresentedToken.FromAuthorization(services.GetRequiredService<IHttpContextAccessor>().HttpContext?.Request.Headers.Authorization);

    /// <summary>Maps the MCP endpoint at <paramref name="pattern"/>; every request to it needs a valid token.</summary>
    public static WebApplication MapSaemMcp(this WebApplication app, string pattern = "/mcp")
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMcp(pattern).RequireAuthorization();
        return app;
    }
}
