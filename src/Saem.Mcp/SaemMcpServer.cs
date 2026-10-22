using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore.Authentication;
using Saem.Ontology;

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

/// <summary>
/// Wires the MCP server: bearer-token authentication against the configured issuer, the one
/// lookup tool, and Streamable HTTP. Configuration lives under <c>Saem</c>: <c>Ontology</c> (the
/// declarations file), <c>Identity</c> (<see cref="IdentityOptions"/>) and <c>Sources</c>
/// (<see cref="SourceOptions"/>).
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

        var ontologyPath = saem["Ontology"];
        services.TryAddSingleton<IOntologyCatalog>(_ => new FileOntologyCatalog(
            string.IsNullOrEmpty(ontologyPath) ? throw new InvalidOperationException("Saem:Ontology names the ontology declarations file.") : ontologyPath));
        services.AddHttpClient();
        services.AddHttpContextAccessor();
        services.TryAddSingleton<ISourceLogins, ConfiguredSourceLogins>();
        services.AddSingleton<LookupTool>();

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
