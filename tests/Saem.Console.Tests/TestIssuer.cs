using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Saem.Console.Tests;

/// <summary>The console's issuer client with its token endpoint answered by <c>handler</c> — no discovery, no network.</summary>
internal static class TestIssuer
{
    public const string TokenEndpoint = "https://issuer.test/token";

    public static ConsoleIssuer For(HttpMessageHandler handler, string clientId = "saem-console", string clientSecret = "") =>
        new(
            new Clients(handler),
            new Monitor(new OpenIdConnectOptions
            {
                ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new OpenIdConnectConfiguration { TokenEndpoint = TokenEndpoint }),
            }),
            Options.Create(new ConsoleIdentityOptions { ClientId = clientId, ClientSecret = clientSecret }));

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Monitor(OpenIdConnectOptions options) : IOptionsMonitor<OpenIdConnectOptions>
    {
        public OpenIdConnectOptions CurrentValue => options;

        public OpenIdConnectOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<OpenIdConnectOptions, string?> listener) => null;
    }
}
