using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using Saem.Query;

namespace Saem.Mcp.Tests;

/// <summary>
/// The server on a loopback port, trusting tokens signed with a key this test holds, reading sources
/// the test supplies. Tokens are minted here with whatever claims a test needs.
/// </summary>
internal sealed class TestServer : IAsyncDisposable
{
    internal const string Issuer = "https://issuer.test/rig";
    internal const string Audience = "saem";

    private static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("a-test-only-signing-key-of-at-least-256-bits!!"));

    private readonly WebApplication _app;
    private readonly string _ontologyFile;

    private TestServer(WebApplication app, string ontologyFile, CapturedLogs logs)
    {
        _app = app;
        _ontologyFile = ontologyFile;
        Logs = logs;
    }

    /// <summary>What the server logged, as formatted lines.</summary>
    public CapturedLogs Logs { get; }

    public Uri Endpoint => new(_app.Urls.First() + "/mcp");

    /// <summary>The server with the test's signing key and in-memory sources, and any further <paramref name="settings"/>.</summary>
    public static Task<TestServer> StartAsync(string ontologyJson, ISourceLogins logins, Dictionary<string, string?>? settings = null) =>
        StartAsync(
            ontologyJson,
            new Dictionary<string, string?>(settings ?? []) { ["Saem:Identity:Authority"] = Issuer, ["Saem:Identity:Audience"] = Audience },
            services =>
            {
                services.AddSingleton(logins);
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
                {
                    // The issuer's keys come from here instead of its discovery document.
                    var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                    configuration.SigningKeys.Add(Key);
                    jwt.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
            });

    /// <summary>The server as configured by <paramref name="settings"/>, with any test overrides.</summary>
    public static async Task<TestServer> StartAsync(string ontologyJson, Dictionary<string, string?> settings, Action<IServiceCollection>? configure = null)
    {
        var ontologyFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(ontologyFile, ontologyJson);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var logs = new CapturedLogs();
        builder.Logging.AddProvider(logs);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(settings) { ["Saem:Ontology"] = ontologyFile });
        configure?.Invoke(builder.Services);
        builder.Services.AddSaemMcp(builder.Configuration);

        var app = builder.Build();
        try
        {
            app.MapSaemMcp();
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            File.Delete(ontologyFile);
            throw;
        }

        return new TestServer(app, ontologyFile, logs);
    }

    public static string Token(string? subject, string audience = Audience, string? actor = null, string[]? groups = null)
    {
        var claims = new Dictionary<string, object>();
        if (subject is not null)
        {
            claims["sub"] = subject;
        }

        if (groups is not null)
        {
            claims["groups"] = groups;
        }

        if (actor is not null)
        {
            claims["act"] = new Dictionary<string, object> { ["sub"] = actor };
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            Claims = claims,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
        });
    }

    public Task<McpClient> ClientAsync(string token, CancellationToken cancellationToken) =>
        McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = Endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
            }),
            cancellationToken: cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        File.Delete(_ontologyFile);
    }
}

/// <summary>Sources held in memory: per person, the rows of each table they may see.</summary>
internal sealed class FakeLogins(Dictionary<string, Dictionary<string, List<SourceRow>>> rowsByPerson, string? failingPerson = null) : ISourceLogins
{
    internal const string FaultDetail = "connection to db-host.internal:5432 refused";

    // A person the test gives no rows to has no login at all, as a person missing from the deployment's
    // logins would; the failing person's source fails when it is opened.
    public ISourceSessions SessionsFor(Person person, PresentedToken token, string lookupId) => new PersonSessions(
        person.Subject == failingPerson
            ? new Dictionary<string, SourceOpener> { ["mes"] = _ => throw new IOException(FaultDetail) }
            : rowsByPerson.TryGetValue(person.Subject, out var tables)
                ? new Dictionary<string, SourceOpener> { ["mes"] = _ => ValueTask.FromResult<ISourceSession>(new Session(tables)) }
                : []);

    public static SourceRow Row(string key, params (string Column, object? Value)[] values) =>
        new(key, values.ToDictionary(v => v.Column, v => JsonSerializer.SerializeToElement(v.Value)));

    private sealed class Session(Dictionary<string, List<SourceRow>> tables) : ISourceSession
    {
        public ValueTask<IReadOnlyList<SourceRow>> ReadAsync(string table, string keyColumn, string key, IReadOnlyList<string> columns, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<SourceRow>>(
                [.. (tables.GetValueOrDefault(table) ?? []).Where(r => r.Key == key)
                    .Select(r => new SourceRow(r.Key, columns.ToDictionary(c => c, c => r.Values[c])))]);

        public IAsyncEnumerable<SourceRow> ScanAsync(string table, string keyColumn, IReadOnlyList<string> columns, CancellationToken cancellationToken = default) =>
            (tables.GetValueOrDefault(table) ?? []).OrderBy(r => r.Key, StringComparer.Ordinal)
                .Select(r => new SourceRow(r.Key, columns.ToDictionary(c => c, c => r.Values[c]))).ToAsyncEnumerable();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal static class Claims
{
    public static ClaimsPrincipal Of(params (string Type, string Value, string ValueType)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value, c.ValueType)), "test"));
}

/// <summary>The server's log lines, kept in memory for a test to read.</summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    public ILogger CreateLogger(string categoryName) => new Logger(_lines);

    public void Dispose()
    {
    }

    private sealed class Logger(System.Collections.Concurrent.ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                lines.Enqueue(formatter(state, exception));
            }
        }
    }
}
