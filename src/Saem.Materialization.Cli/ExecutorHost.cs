using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Saem.Materialization.Cli;

/// <summary>
/// The executor kept running (saem D-90): one group's passes and checks, asked for over HTTP by the administration
/// console, which holds no key to the group's store and no account at its sources. A caller is whoever presents one of
/// the executor's secrets; who it says it asks for (<c>requestedBy</c>) is the console's word, recorded and not checked.
/// </summary>
internal static class ExecutorHost
{
    /// <summary>How large a request may be: it names a kind and a person, nothing more.</summary>
    private const int MaxRequestBytes = 4096;

    private const int MaxRequestedByLength = 256;

    /// <summary>The executor, built and not yet started.</summary>
    /// <param name="listen">The address to serve on.</param>
    /// <param name="secrets">The bearer secrets a caller may present; there are at most two, so one is replaced without refusing callers.</param>
    /// <param name="kinds">The kinds of job it does, by the name a request gives.</param>
    public static WebApplication Build(Uri listen, IReadOnlyList<string> secrets, IReadOnlyDictionary<string, JobWork> kinds, TimeProvider? time = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(listen.GetLeftPart(UriPartial.Authority));
        // Each request is not worth a line: what is worth one is a job that fails or the host that stops.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        var app = builder.Build();
        var store = new JobStore(time ?? TimeProvider.System, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Saem.Executor"));
        var stopped = app.Lifetime.ApplicationStopping;
        // A job still writing when the executor is told to stop is asked to stop, and given time to leave the store consistent.
        stopped.Register(() => store.DrainAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult());
        var digests = secrets.Select(s => SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToArray();

        app.Use(async (http, next) =>
        {
            if (!Presents(http.Request, digests))
            {
                http.Response.Headers.WWWAuthenticate = "Bearer";
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(http);
        });

        app.MapPost("/jobs", async (HttpContext http, CancellationToken aborted) =>
        {
            http.Features.GetRequiredFeature<IHttpMaxRequestBodySizeFeature>().MaxRequestBodySize = MaxRequestBytes;
            JobRequest? request;
            try
            {
                request = await http.Request.ReadFromJsonAsync<JobRequest>(aborted);
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or BadHttpRequestException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = "The request is a JSON object with a kind and requestedBy." });
            }

            if (request?.Kind is not { Length: > 0 } kind || !kinds.TryGetValue(kind, out var work))
            {
                return Results.BadRequest(new { error = $"kind is one of: {string.Join(", ", kinds.Keys.Order(StringComparer.Ordinal))}." });
            }

            if (request.RequestedBy is not { Length: > 0 and <= MaxRequestedByLength } requestedBy || requestedBy.Any(char.IsControl))
            {
                return Results.BadRequest(new { error = $"requestedBy names who asked, in up to {MaxRequestedByLength} characters." });
            }

            if (!store.TryStart(kind, requestedBy, work, stopped, out var job))
            {
                return Results.Json(new { error = "Another job is running.", running = job }, statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Json(job, statusCode: StatusCodes.Status202Accepted);
        });
        app.MapGet("/jobs", () => store.Recent());
        app.MapGet("/jobs/{id}", (string id) => store.Get(id) is { } job ? Results.Json(job) : Results.NotFound());
        app.MapPost("/jobs/{id}/cancel", (string id) => store.Cancel(id) switch
        {
            null => Results.NotFound(),
            false => Results.Json(new { error = "The job is not running." }, statusCode: StatusCodes.Status409Conflict),
            true => Results.Accepted(),
        });

        return app;
    }

    private static bool Presents(HttpRequest request, byte[][] digests)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim()));
        var matches = false;

        // Every secret is compared, whichever matches first, so the time taken says nothing about which one is right.
        foreach (var digest in digests)
        {
            matches |= CryptographicOperations.FixedTimeEquals(presented, digest);
        }

        return matches;
    }

    private sealed record JobRequest(string? Kind, string? RequestedBy);
}
