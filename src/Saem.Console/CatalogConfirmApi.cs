using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Saem.Ontology;

namespace Saem.Console;

/// <summary>
/// A builder's draft of the catalog becomes the catalog in force (saem D-88 C4, D-91): the draft is shown against the
/// catalog in force — what it adds, what can break — and confirming it replaces the catalog file with exactly the draft
/// that was shown, if and only if the catalog in force is still the one the builder started from (an optimistic lock on
/// the catalog version; builders write one at a time). Each confirmation leaves one line in the confirmations file.
/// </summary>
internal static class CatalogConfirmApi
{
    // One console process confirms one catalog; the check and the replacement happen under this.
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public static void Map(RouteGroupBuilder api)
    {
        var draft = api.MapGroup("/catalog").RequireAuthorization(ConsoleRoles.Builder);

        draft.MapGet("/draft", async (IOptions<ConsoleCatalogOptions> options, CancellationToken ct) =>
            await Show(options.Value, ct));

        draft.MapPost("/confirm", async (ClaimsPrincipal user, IOptions<ConsoleCatalogOptions> options, TimeProvider time, JsonElement request, CancellationToken ct) =>
        {
            if (user.FindFirstValue("sub") is not { Length: > 0 } subject)
            {
                return TypedResults.Problem(title: "The token names no one.", detail: "A confirmation is made in the name of the person confirming, and the token has no subject.", statusCode: StatusCodes.Status403Forbidden);
            }

            if (Read(request) is not { } ask)
            {
                return TypedResults.Problem(
                    title: "The confirmation is refused.",
                    detail: "Send base and draft, the catalog versions the builder saw, and acknowledgeBreaking: true to let breaking changes through.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            await OneAtATime.WaitAsync(ct);
            try
            {
                return await Confirm(options.Value, subject, time.GetUtcNow(), ask, ct);
            }
            finally
            {
                OneAtATime.Release();
            }
        });
    }

    private static async Task<IResult> Show(ConsoleCatalogOptions options, CancellationToken ct)
    {
        if (!Configured(options))
        {
            return NoDraft();
        }

        var seen = await Compare(options, ct);
        return seen.Problem ?? TypedResults.Ok(new CatalogDraftView(
            seen.InForce!.Version,
            seen.Draft!.Version,
            [.. seen.Changes!.Select(c => new CatalogDraftChange(c.Kind == ChangeKind.Breaking ? "breaking" : "additive", c.Declaration, c.Change))],
            seen.Changes!.Count(c => c.Kind == ChangeKind.Breaking)));
    }

    private static async Task<IResult> Confirm(ConsoleCatalogOptions options, string subject, DateTimeOffset at, ConfirmAsk ask, CancellationToken ct)
    {
        if (!Configured(options))
        {
            return NoDraft();
        }

        var seen = await Compare(options, ct);
        if (seen.Problem is not null)
        {
            return seen.Problem;
        }

        var breaking = seen.Changes!.Count(c => c.Kind == ChangeKind.Breaking);
        if (seen.InForce!.Version != ask.Base)
        {
            return Conflict("stale-base", $"The catalog in force is {seen.InForce.Version}, not {ask.Base}: someone confirmed or edited it since you started. Read the draft against it again.");
        }

        if (seen.Draft!.Version != ask.Draft)
        {
            return Conflict("stale-draft", $"The draft is {seen.Draft.Version}, not {ask.Draft}: it was changed since you read it. Read it again before confirming.");
        }

        if (seen.Draft.Version == seen.InForce.Version)
        {
            return Conflict("nothing-to-confirm", "The draft declares what the catalog in force already declares.");
        }

        if (breaking > 0 && !ask.AcknowledgeBreaking)
        {
            return Conflict("breaking", $"The draft has {breaking} breaking change(s); confirm with acknowledgeBreaking: true to let them through.");
        }

        try
        {
            // The record is opened before the catalog is replaced, so a record that cannot be kept stops the confirmation
            // rather than following it.
            var record = new FileStream(options.Confirmations, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using (record.ConfigureAwait(false))
            {
                await ReplaceAsync(options.Ontology, seen.DraftBytes!, ct);
                var line = new JsonObject
                {
                    ["at"] = at.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    ["by"] = subject,
                    ["from"] = seen.InForce.Version,
                    ["to"] = seen.Draft.Version,
                    ["breaking"] = breaking,
                    ["additive"] = seen.Changes!.Count - breaking,
                    ["acknowledgedBreaking"] = breaking > 0,
                };
                await record.WriteAsync(Encoding.UTF8.GetBytes(line.ToJsonString() + "\n"), CancellationToken.None);
                await record.FlushAsync(CancellationToken.None);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return TypedResults.Problem(
                title: "The catalog could not be written.",
                detail: $"The console must be able to write the catalog file and the confirmations file: {e.Message}",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        return TypedResults.Ok(new CatalogConfirmed(seen.InForce.Version, seen.Draft.Version, breaking));
    }

    /// <summary>Puts the draft's bytes where the catalog is, whole or not at all.</summary>
    private static async Task ReplaceAsync(string catalogPath, byte[] bytes, CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(catalogPath))!;
        var staged = Path.Combine(folder, $".{Path.GetFileName(catalogPath)}.{Guid.NewGuid():N}.confirm");
        try
        {
            await File.WriteAllBytesAsync(staged, bytes, ct);
            File.Move(staged, catalogPath, overwrite: true);
        }
        finally
        {
            File.Delete(staged);
        }
    }

    private static bool Configured(ConsoleCatalogOptions options) => options.Draft.Length > 0;

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult NoDraft() => TypedResults.Problem(
        title: "No draft is set.",
        detail: "Saem:Console:Draft names the draft file and Saem:Console:Confirmations the file each confirmation is recorded in.",
        statusCode: StatusCodes.Status404NotFound);

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult Conflict(string code, string detail) => TypedResults.Problem(
        title: "The confirmation is not made.",
        detail: detail,
        statusCode: StatusCodes.Status409Conflict,
        extensions: new Dictionary<string, object?> { ["code"] = code });

    private static ConfirmAsk? Read(JsonElement request) =>
        request.ValueKind == JsonValueKind.Object
        && request.TryGetProperty("base", out var b) && b.ValueKind == JsonValueKind.String && b.GetString() is { Length: > 0 } baseVersion
        && request.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { Length: > 0 } draftVersion
            ? new ConfirmAsk(baseVersion, draftVersion, request.TryGetProperty("acknowledgeBreaking", out var a) && a.ValueKind == JsonValueKind.True)
            : null;

    /// <summary>Reads the catalog in force and the draft, and what separates them.</summary>
    private static async Task<Seen> Compare(ConsoleCatalogOptions options, CancellationToken ct)
    {
        Ontology.Ontology inForce;
        try
        {
            inForce = await new FileOntologyCatalog(options.Ontology).GetAsync(ct);
        }
        catch (OntologyDeclarationException e)
        {
            return new Seen { Problem = TypedResults.Problem(title: "The catalog is refused.", detail: string.Join(" ", e.Problems), statusCode: StatusCodes.Status503ServiceUnavailable) };
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(options.Draft, ct);
        }
        catch (FileNotFoundException)
        {
            return new Seen { Problem = TypedResults.Problem(title: "There is no draft.", detail: "The draft file does not exist.", statusCode: StatusCodes.Status404NotFound) };
        }

        Ontology.Ontology draft;
        try
        {
            var declarations = JsonSerializer.Deserialize(bytes, OntologyJson.Default.IReadOnlyListDeclaration)
                ?? throw new OntologyDeclarationException("The draft holds null, not an array of declarations.");
            draft = Ontology.Ontology.Create(declarations);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return new Seen { Problem = TypedResults.Problem(title: "The draft is refused.", detail: e.Message, statusCode: StatusCodes.Status422UnprocessableEntity) };
        }
        catch (OntologyDeclarationException e)
        {
            return new Seen { Problem = TypedResults.Problem(title: "The draft is refused.", detail: string.Join(" ", e.Problems), statusCode: StatusCodes.Status422UnprocessableEntity) };
        }

        return new Seen { InForce = inForce, Draft = draft, DraftBytes = bytes, Changes = OntologyChanges.Between(inForce, draft) };
    }

    private sealed record ConfirmAsk(string Base, string Draft, bool AcknowledgeBreaking);

    private sealed class Seen
    {
        public IResult? Problem { get; init; }

        public Ontology.Ontology? InForce { get; init; }

        public Ontology.Ontology? Draft { get; init; }

        public byte[]? DraftBytes { get; init; }

        public IReadOnlyList<OntologyChange>? Changes { get; init; }
    }
}

/// <summary>A draft read against the catalog in force: both versions, and each change from the one to the other.</summary>
/// <param name="InForce">The version of the catalog in force — the <c>base</c> a confirmation names.</param>
/// <param name="Draft">The version of the draft — the <c>draft</c> a confirmation names.</param>
/// <param name="Changes">Breaking changes first, then the ones that only add vocabulary.</param>
/// <param name="Breaking">How many of the changes can break what was built on the catalog in force.</param>
public sealed record CatalogDraftView(string InForce, string Draft, IReadOnlyList<CatalogDraftChange> Changes, int Breaking);

/// <summary>One change between the catalog in force and a draft.</summary>
/// <param name="Kind"><c>breaking</c> or <c>additive</c>.</param>
/// <param name="Declaration">The declaration it is about, e.g. <c>entityType Supplier</c>.</param>
/// <param name="Change">What changed.</param>
public sealed record CatalogDraftChange(string Kind, string Declaration, string Change);

/// <summary>The catalog version a confirmation replaced and the one now in force.</summary>
public sealed record CatalogConfirmed(string From, string To, int Breaking);
