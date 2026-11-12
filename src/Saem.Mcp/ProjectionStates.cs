using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Saem.Contracts;
using Saem.Query;

namespace Saem.Mcp;

/// <summary>
/// A source system whose tables are a Formbase's projections: the Formbase that keeps the documents, and
/// which form type each table is projected from.
/// </summary>
public sealed class ProjectionSourceOptions
{
    /// <summary>The Formbase that projects into the system's tables.</summary>
    public string Url { get; set; } = "";

    /// <summary>The Formbase namespace, when the instance serves several; omitted, the instance's own.</summary>
    public string? Namespace { get; set; }

    /// <summary>One of the Formbase's access keys, sent as a bearer token; omitted when it asks for none.</summary>
    public string? AccessKey { get; set; }

    /// <summary>The form type each projected table is projected from, by table name as the ontology declares it.</summary>
    public Dictionary<string, string> FormTypes { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Asks each configured Formbase whether a table's projection is behind its documents
/// (<c>GET formtypes/{type}/projection</c>) and, for a projection that is current, whether it left documents out
/// (<c>GET formtypes/{type}/projection/skips</c>). The state is about the form type, not about anyone's rows, so it
/// is asked over the deployment's own key. A Formbase that cannot be asked leaves the state unsaid — the
/// facts read are still the person's own reads — and is logged for whoever runs it.
/// </summary>
internal sealed partial class FormbaseProjectionStates : IProjectionStates
{
    private const string NamespaceHeader = "Formbase-Namespace";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, (HttpClient Http, ProjectionSourceOptions Options)> _systems = new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    public FormbaseProjectionStates(IOptions<SourceOptions> options, IHttpClientFactory httpClients, ILogger<FormbaseProjectionStates>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        foreach (var (system, source) in options.Value.Projections)
        {
            if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var url))
            {
                throw new InvalidOperationException($"The system '{system}' names no Formbase: Saem:Sources:Projections:{system}:Url is the Formbase that projects its tables.");
            }

            var http = httpClients.CreateClient("Projections:" + system);
            http.BaseAddress = url.AbsoluteUri.EndsWith('/') ? url : new Uri(url.AbsoluteUri + "/");
            if (!string.IsNullOrEmpty(source.AccessKey))
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", source.AccessKey);
            }

            _systems.Add(system, (http, source));
        }
    }

    public async ValueTask<SourceProjection?> StateOfAsync(string system, string table, CancellationToken cancellationToken = default)
    {
        if (!_systems.TryGetValue(system, out var source) || !source.Options.FormTypes.TryGetValue(table, out var formType))
        {
            return null;
        }

        try
        {
            var status = await ReadAsync(source, $"formtypes/{Uri.EscapeDataString(formType)}/projection", system, table, cancellationToken).ConfigureAwait(false);
            if (status is null)
            {
                return null;
            }

            return status.Value.GetProperty("state").GetString() switch
            {
                "projected" => await SkipsOfAsync(source, formType, system, table, cancellationToken).ConfigureAwait(false),
                "stale" => SourceProjection.Stale,
                "unverified" => SourceProjection.Unverified,
                "notProjected" => SourceProjection.NotProjected,
                var other => Unknown(system, table, $"state '{other}'"),
            };
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
            or TaskCanceledException { InnerException: TimeoutException })
        {
            LogUnknown(_logger, system, table, e.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// A projection that reached the head of the stream is current, not complete: a document its declaration could
    /// not map is recorded as skipped and is in no row. The durable count is asked of the Formbase —
    /// <c>lastRun</c> on the state is only what the host instance itself saw since it started, and says nothing
    /// after a restart. A Formbase that cannot say leaves the state unsaid, as for the state itself.
    /// </summary>
    private async ValueTask<SourceProjection?> SkipsOfAsync((HttpClient Http, ProjectionSourceOptions Options) source, string formType, string system, string table, CancellationToken cancellationToken)
    {
        var skips = await ReadAsync(source, $"formtypes/{Uri.EscapeDataString(formType)}/projection/skips", system, table, cancellationToken).ConfigureAwait(false);
        return skips?.GetProperty("count").GetInt32() > 0 ? SourceProjection.Incomplete : null;
    }

    private async ValueTask<JsonElement?> ReadAsync((HttpClient Http, ProjectionSourceOptions Options) source, string path, string system, string table, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (source.Options.Namespace is { } ns)
        {
            request.Headers.Add(NamespaceHeader, ns);
        }

        using var response = await source.Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            LogUnknown(_logger, system, table, $"HTTP {(int)response.StatusCode}");
            return null;
        }

        return await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken).ConfigureAwait(false);
    }

    private SourceProjection? Unknown(string system, string table, string why)
    {
        LogUnknown(_logger, system, table, why);
        return null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The projection state of '{System}'.'{Table}' could not be learned ({Why}); answers read from it say nothing of it.")]
    private static partial void LogUnknown(ILogger logger, string system, string table, string why);
}
