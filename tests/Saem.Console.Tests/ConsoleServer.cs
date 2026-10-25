using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Saem.Console.Tests;

/// <summary>The console on a loopback port, trusting a test issuer whose signing key the test holds.</summary>
internal sealed class ConsoleServer : IAsyncDisposable
{
    internal const string Issuer = "https://issuer.test/rig";
    internal const string Audience = "saem-console";

    private static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("a-test-only-signing-key-of-at-least-256-bits!!"));

    private readonly WebApplication _app;

    private ConsoleServer(WebApplication app) => _app = app;

    /// <summary>The catalog file the console reads — the test may rewrite it; it is read on every request.</summary>
    public string OntologyFile { get; private init; } = "";

    public Uri Address => new(_app.Urls.First() + "/");

    /// <summary>
    /// The console with the identity and roles settings only — no source and no store is configured, so whatever it
    /// serves it serves without either (saem D-89).
    /// </summary>
    public static async Task<ConsoleServer> StartAsync(string? builders = "ontology-builders", string? liveIssuer = null, string ontologyJson = "[]", Action<IServiceCollection>? configure = null)
    {
        var ontologyFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(ontologyFile, ontologyJson);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Saem:Console:Identity:Authority"] = liveIssuer ?? Issuer,
            ["Saem:Console:Identity:RequireHttpsMetadata"] = liveIssuer is null ? "true" : "false",
            ["Saem:Console:Identity:Audience"] = Audience,
            ["Saem:Console:Identity:ClientId"] = "saem-console",
            ["Saem:Console:Identity:ClientSecret"] = "rig-local-only",
            ["Saem:Console:Identity:Scopes:0"] = "saem-rig",
            ["Saem:Console:Roles:Auditors:0"] = "privacy-office",
            ["Saem:Console:Ontology"] = ontologyFile,
        });
        if (builders is not null)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Saem:Console:Roles:Builders:0"] = builders });
        }
        builder.Services.AddSaemConsole(builder.Configuration);
        configure?.Invoke(builder.Services);
        if (liveIssuer is null)
        {
            builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                configuration.SigningKeys.Add(Key);
                jwt.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            });
            builder.Services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, oidc =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = Issuer, AuthorizationEndpoint = Issuer + "/authorize", TokenEndpoint = Issuer + "/token" };
                configuration.SigningKeys.Add(Key);
                oidc.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            });
        }

        var app = builder.Build();
        app.MapSaemConsole();
        await app.StartAsync();
        return new ConsoleServer(app) { OntologyFile = ontologyFile };
    }

    public HttpClient Client(string? token = null, HttpMessageHandler? handler = null)
    {
        var client = handler is null ? new HttpClient { BaseAddress = Address } : new HttpClient(handler) { BaseAddress = Address };
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    public static string Token(string subject, string[]? groups = null, string audience = Audience, Dictionary<string, object>? more = null) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            Claims = new Dictionary<string, object>(more ?? []) { ["sub"] = subject, ["groups"] = groups ?? [] },
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
        });

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        File.Delete(OntologyFile);
    }
}
