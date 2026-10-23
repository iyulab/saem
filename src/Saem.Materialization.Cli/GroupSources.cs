using System.Text.Json;
using Saem.Connectors.MorphDb;
using Saem.Connectors.MySql;
using Saem.Connectors.Postgres;
using Saem.Query;

namespace Saem.Materialization.Cli;

/// <summary>
/// How one group's account reads each source system, from the sources file and the environment: where the
/// system is and the account's login name come from the file, the account's secret from
/// <c>SAEM_SOURCE_SECRET_&lt;SYSTEM&gt;</c>. A system whose secret is not set is refused before anything is read.
/// </summary>
internal sealed class GroupSources : IDisposable
{
    private readonly List<HttpClient> _clients = [];

    private GroupSources()
    {
    }

    /// <summary>An opener per system, each logging in as the group's account.</summary>
    public Dictionary<string, SourceOpener> Openers { get; } = new(StringComparer.Ordinal);

    /// <exception cref="MaterializeUsageException">The file is not a sources file, or a system's secret is not set.</exception>
    public static GroupSources Load(string json, Func<string, string?> environment)
    {
        var sources = new GroupSources();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new MaterializeUsageException("The sources file is a JSON object of systems.");
            }

            foreach (var system in document.RootElement.EnumerateObject())
            {
                var secret = environment(MaterializeOptions.SecretVariable(system.Name)) is { Length: > 0 } value
                    ? value
                    : throw new MaterializeUsageException($"The system '{system.Name}' has no secret: set {MaterializeOptions.SecretVariable(system.Name)}.");
                sources.Openers.Add(system.Name, sources.Opener(system.Name, system.Value, secret));
            }
        }
        catch (Exception e) when (e is JsonException or ArgumentException or FormatException)
        {
            sources.Dispose();
            throw new MaterializeUsageException($"The sources file cannot be read: {e.Message}");
        }
        catch
        {
            sources.Dispose();
            throw;
        }

        return sources;
    }

    private SourceOpener Opener(string system, JsonElement source, string secret)
    {
        ValueTask<string> Secret(CancellationToken _) => ValueTask.FromResult(secret);

        string Text(string property) => source.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw new MaterializeUsageException($"The system '{system}' names no '{property}'.");

        if (source.ValueKind != JsonValueKind.Object)
        {
            throw new MaterializeUsageException($"The system '{system}' is not an object.");
        }

        if (source.TryGetProperty("postgres", out _))
        {
            return new PostgresSource(Text("postgres")).As(Text("user"), Secret, "saem-materialize");
        }

        if (source.TryGetProperty("mysql", out _))
        {
            return new MySqlSource(Text("mysql")).As(Text("user"), Secret, "saem-materialize");
        }

        if (source.TryGetProperty("morphdb", out _))
        {
            var url = Text("morphdb");
            var http = new HttpClient { BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/") };
            _clients.Add(http);
            return new MorphDbSource(http, Guid.Parse(Text("projectId"))).As(Secret);
        }

        throw new MaterializeUsageException($"The system '{system}' says neither postgres, mysql nor morphdb.");
    }

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }
    }
}
