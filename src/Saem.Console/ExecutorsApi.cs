using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Saem.Console;

/// <summary>
/// Where a group's executor listens and the secret that lets the console ask it for work (saem D-90). The secret is a
/// credential to ask an executor, not to the group's store or its sources: those stay with the executor.
/// </summary>
public sealed class ConsoleExecutorOptions
{
    /// <summary>The executor's address, e.g. <c>https://executor.plant.example:8443/</c>.</summary>
    public string Address { get; set; } = "";

    /// <summary>One of the executor's secrets (<c>SAEM_EXECUTOR_SECRET</c> there).</summary>
    public string Secret { get; set; } = "";
}

/// <summary>The executors the console may ask, by the group each one serves (<c>Saem:Console:Executors:&lt;group&gt;</c>).</summary>
public sealed class ConsoleExecutors : Dictionary<string, ConsoleExecutorOptions>
{
    /// <summary>The shortest secret an executor takes.</summary>
    public const int MinimumSecretLength = 24;

    public ConsoleExecutors()
        : base(StringComparer.OrdinalIgnoreCase)
    {
    }

    /// <summary>Why the settings cannot be used, or <see langword="null"/>.</summary>
    internal string? Problem() => this
        .Select(e => !Uri.TryCreate(e.Value.Address, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")
            ? $"Saem:Console:Executors:{e.Key}:Address takes an http(s) address."
            : e.Value.Secret.Length < MinimumSecretLength ? $"Saem:Console:Executors:{e.Key}:Secret must be at least {MinimumSecretLength} characters." : null)
        .FirstOrDefault(p => p is not null);
}

/// <summary>An executor's answer to the console: its status, and the JSON it sent, if any.</summary>
public sealed record ExecutorReply(int Status, JsonNode? Body);

/// <summary>The executor of a group could not be asked, or did not take the console's secret.</summary>
public sealed class ExecutorUnavailableException(string message) : Exception(message);

/// <summary>Asks the executor of a group for work.</summary>
public interface IConsoleExecutors
{
    /// <summary>The groups the console has an executor for.</summary>
    IReadOnlyCollection<string> Groups { get; }

    /// <summary>One request to the executor of <paramref name="group"/>, under its <c>/jobs</c> surface.</summary>
    /// <exception cref="ExecutorUnavailableException">The executor did not answer, answered with something else, or refused the secret.</exception>
    Task<ExecutorReply> SendAsync(string group, HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken);
}

/// <summary>The executors over HTTP, each asked with its own bearer secret.</summary>
internal sealed class HttpConsoleExecutors(IHttpClientFactory clients, IOptions<ConsoleExecutors> executors) : IConsoleExecutors
{
    /// <summary>How long an executor has to answer: a request names a job and returns at once, so a long wait is a fault.</summary>
    internal static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public IReadOnlyCollection<string> Groups => executors.Value.Keys;

    public async Task<ExecutorReply> SendAsync(string group, HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
    {
        var executor = executors.Value.TryGetValue(group, out var found) ? found : throw new ArgumentException($"No executor is set for group '{group}'.", nameof(group));
        using var request = new HttpRequestMessage(method, new Uri(new Uri(executor.Address.EndsWith('/') ? executor.Address : executor.Address + "/"), path));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", executor.Secret);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        }

        using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        patience.CancelAfter(Patience);
        try
        {
            using var http = clients.CreateClient(nameof(HttpConsoleExecutors));
            using var response = await http.SendAsync(request, patience.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new ExecutorUnavailableException($"The executor for group '{group}' refused the console's secret; check Saem:Console:Executors:{group}:Secret.");
            }

            var text = await response.Content.ReadAsStringAsync(patience.Token).ConfigureAwait(false);
            return new ExecutorReply((int)response.StatusCode, string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text));
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new ExecutorUnavailableException($"The executor for group '{group}' did not answer.");
        }
    }
}

/// <summary>
/// What a builder may ask a group's executor for, through the console (saem D-90 · PLAN slice 5): the console checks the
/// request, names the caller as who asked, and gives the answer back. It holds no key to the group's store and reads no
/// source — the executor, which does, is the group's.
/// </summary>
internal static partial class ExecutorsApi
{
    /// <summary>The kinds of job an executor does, as a request names them.</summary>
    internal static readonly string[] Kinds = ["verify", "check", "candidates", "pass", "changed-pass", "fit", "judge"];

    private const int MaxArgumentLength = 2048;

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex JobId();

    public static void Map(RouteGroupBuilder api)
    {
        var executors = api.MapGroup("/executors").RequireAuthorization(ConsoleRoles.Builder);

        executors.MapGet("", (IConsoleExecutors known) => TypedResults.Ok(known.Groups.Order(StringComparer.OrdinalIgnoreCase).Select(g => new { group = g })));

        executors.MapGet("/{group}/jobs", (string group, IConsoleExecutors known, CancellationToken ct) =>
            Relay(known, group, HttpMethod.Get, "jobs", null, ct));

        executors.MapGet("/{group}/jobs/{id}", (string group, string id, IConsoleExecutors known, CancellationToken ct) =>
            JobId().IsMatch(id) ? Relay(known, group, HttpMethod.Get, $"jobs/{id}", null, ct) : Task.FromResult<IResult>(NotFound("job")));

        executors.MapPost("/{group}/jobs/{id}/cancel", (string group, string id, IConsoleExecutors known, CancellationToken ct) =>
            JobId().IsMatch(id) ? Relay(known, group, HttpMethod.Post, $"jobs/{id}/cancel", null, ct) : Task.FromResult<IResult>(NotFound("job")));

        executors.MapPost("/{group}/jobs", async (string group, ClaimsPrincipal user, IConsoleExecutors known, JsonElement request, CancellationToken ct) =>
        {
            if (user.FindFirstValue("sub") is not { Length: > 0 } subject)
            {
                return TypedResults.Problem(title: "The token names no one.", detail: "A job is asked in the name of the person asking, and the token has no subject.", statusCode: StatusCodes.Status403Forbidden);
            }

            return Ask(request, subject) switch
            {
                (null, var problem) => TypedResults.Problem(title: "The job is refused.", detail: problem, statusCode: StatusCodes.Status400BadRequest),
                (var body, _) => await Relay(known, group, HttpMethod.Post, "jobs", body, ct),
            };
        });
    }

    /// <summary>The request to the executor for what a builder asked — or why it cannot be asked.</summary>
    internal static (JsonObject? Body, string? Problem) Ask(JsonElement request, string subject)
    {
        if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String
            || kindElement.GetString() is not { } kind || !Kinds.Contains(kind, StringComparer.Ordinal))
        {
            return (null, $"kind is one of: {string.Join(", ", Kinds)}.");
        }

        var given = request.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object ? arguments : default;
        var kept = new JsonObject();
        foreach (var (name, needed) in kind switch
        {
            "fit" => [("identity", true)],
            "judge" => [("identity", true), ("first", true), ("second", true)],
            _ => Array.Empty<(string, bool)>(),
        })
        {
            var value = given.ValueKind == JsonValueKind.Object && given.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.String ? found.GetString() : null;
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaxArgumentLength)
            {
                return needed ? (null, $"{kind} needs {name}, in up to {MaxArgumentLength} characters.") : (null, null);
            }

            kept[name] = value;
        }

        if (kind == "judge")
        {
            if (given.ValueKind != JsonValueKind.Object || !given.TryGetProperty("same", out var same) || same.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return (null, "judge needs same: true when the pair is one thing, false when it is two.");
            }

            kept["same"] = same.GetBoolean();
        }

        return (new JsonObject { ["kind"] = kind, ["requestedBy"] = subject, ["arguments"] = kept }, null);
    }

    private static async Task<IResult> Relay(IConsoleExecutors known, string group, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        if (!known.Groups.Contains(group, StringComparer.OrdinalIgnoreCase))
        {
            return NotFound("executor");
        }

        try
        {
            var reply = await known.SendAsync(group, method, path, body, ct);
            return reply.Status switch
            {
                >= 200 and < 300 or 400 or 409 => Json(reply.Status, Shape(reply.Body)),
                404 => NotFound("job"),
                _ => TypedResults.Problem(title: "The executor could not do that.", detail: $"The executor for group '{group}' answered {reply.Status}.", statusCode: StatusCodes.Status502BadGateway),
            };
        }
        catch (ExecutorUnavailableException e)
        {
            return TypedResults.Problem(title: "The executor could not be asked.", detail: e.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static IResult Json(int status, JsonNode? body) =>
        body is null ? Results.StatusCode(status) : Results.Text(body.ToJsonString(), "application/json", statusCode: status);

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult NotFound(string what) => TypedResults.Problem(title: $"No such {what}.", statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// A job as a builder sees it. A check's lines and report cite the records that broke a declaration, which the group's
    /// account read and the builder may not be able to read at the source — they are cut as they are from the report file
    /// the console shows (saem constitution §2), and the report is JSON, not a string holding JSON.
    /// </summary>
    internal static JsonNode? Shape(JsonNode? reply)
    {
        switch (reply)
        {
            case JsonArray jobs:
                return new JsonArray([.. jobs.Select(Shape)]);
            case JsonObject job:
                var shown = (JsonObject)job.DeepClone();
                if (shown["running"] is JsonObject running)
                {
                    shown["running"] = Shape(running);
                }

                if (shown["report"] is JsonValue text && text.TryGetValue<string>(out var report))
                {
                    try
                    {
                        shown["report"] = JsonNode.Parse(report) is JsonObject parsed ? OverviewApi.WithoutRecords(parsed) : null;
                    }
                    catch (JsonException)
                    {
                        shown["report"] = null;
                    }
                }

                if (shown["kind"]?.GetValue<string>() == "check")
                {
                    shown["output"] = "";
                    shown["outputWithheld"] = true;
                }

                return shown;
            default:
                return reply;
        }
    }
}
