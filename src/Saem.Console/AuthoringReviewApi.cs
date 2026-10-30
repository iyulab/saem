using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Saem.Ontology;

namespace Saem.Console;

/// <summary>
/// A builder reviews the declarations an authoring pass proposed, one by one (saem D-88 C4). The draft file is the state of
/// the review: taking a proposal puts its declaration in the draft, leaving one takes it out, and nothing else records the
/// decision — the draft shown is the review so far, and confirming it (<see cref="CatalogConfirmApi"/>) is what puts it in
/// force. A proposal the draft holds changed from what was proposed is shown as edited, with both. Reviews are written one
/// at a time and only against the draft the builder read: a draft that moved since is refused, not overwritten.
/// </summary>
internal static class AuthoringReviewApi
{
    public static void Map(RouteGroupBuilder api)
    {
        var review = api.MapGroup("/authoring").RequireAuthorization(ConsoleRoles.Builder);

        review.MapGet("/review", async (IOptions<ConsoleCatalogOptions> options, CancellationToken ct) =>
            await Show(options.Value, ct));

        review.MapPost("/review", async (IOptions<ConsoleCatalogOptions> options, JsonElement request, CancellationToken ct) =>
        {
            if (Read(request) is not { } ask)
            {
                return TypedResults.Problem(
                    title: "The review is refused.",
                    detail: "Send draft, the draft version the builder saw, and take and/or leave, arrays of proposal ids such as entityType:Supplier.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            await CatalogConfirmApi.OneAtATime.WaitAsync(ct);
            try
            {
                return await Decide(options.Value, ask, ct);
            }
            finally
            {
                CatalogConfirmApi.OneAtATime.Release();
            }
        });
    }

    private static async Task<IResult> Show(ConsoleCatalogOptions options, CancellationToken ct)
    {
        var loaded = await Load(options, ct);
        return loaded.Problem ?? TypedResults.Ok(View(loaded));
    }

    private static async Task<IResult> Decide(ConsoleCatalogOptions options, ReviewAsk ask, CancellationToken ct)
    {
        var loaded = await Load(options, ct);
        if (loaded.Problem is not null)
        {
            return loaded.Problem;
        }

        var proposed = loaded.Proposals!.Added.ToDictionary(ProposedDeclarations.IdOf, StringComparer.Ordinal);
        if (ask.Take.Concat(ask.Leave).FirstOrDefault(id => !proposed.ContainsKey(id)) is { } unknown)
        {
            return TypedResults.Problem(
                title: "The review is refused.",
                detail: $"'{unknown}' is not one of the pass's proposals.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = "unknown-proposal" });
        }

        if (ask.Take.Intersect(ask.Leave, StringComparer.Ordinal).FirstOrDefault() is { } both)
        {
            return TypedResults.Problem(
                title: "The review is refused.",
                detail: $"'{both}' is both taken and left.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = "contradiction" });
        }

        if (loaded.Seen!.Draft!.Version != ask.Draft)
        {
            return TypedResults.Problem(
                title: "The review is not made.",
                detail: $"The draft is {loaded.Seen.Draft.Version}, not {ask.Draft}: it was changed since you read it. Read it again before reviewing.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = "stale-draft" });
        }

        var left = ask.Leave.ToHashSet(StringComparer.Ordinal);
        var declarations = loaded.Seen.DraftDeclarations!.Where(d => !left.Contains(ProposedDeclarations.IdOf(d))).ToList();
        var held = declarations.Select(ProposedDeclarations.IdOf).ToHashSet(StringComparer.Ordinal);
        foreach (var id in ask.Take.Where(id => !held.Contains(id)))
        {
            declarations.Add(proposed[id]);
        }

        Ontology.Ontology reviewed;
        try
        {
            reviewed = Ontology.Ontology.Create(declarations);
        }
        catch (OntologyDeclarationException e)
        {
            return TypedResults.Problem(
                title: "The review is not made.",
                detail: $"The draft would no longer be consistent: {string.Join(" ", e.Problems)}",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?> { ["code"] = "inconsistent" });
        }

        if (reviewed.Version != loaded.Seen.Draft.Version)
        {
            try
            {
                await CatalogConfirmApi.ReplaceAsync(options.Draft, System.Text.Encoding.UTF8.GetBytes(OntologyFile.ToIndentedJson(declarations)), ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return TypedResults.Problem(
                    title: "The draft could not be written.",
                    detail: $"The console must be able to write the draft file: {e.Message}",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        }

        return await Show(options, ct);
    }

    private static AuthoringReviewView View(Loaded loaded)
    {
        var inDraft = loaded.Seen!.DraftDeclarations!.ToDictionary(ProposedDeclarations.IdOf, StringComparer.Ordinal);
        var proposals = loaded.Proposals!.Added.Select(declaration =>
        {
            var id = ProposedDeclarations.IdOf(declaration);
            var proposedNode = Node(declaration);
            if (!inDraft.TryGetValue(id, out var held))
            {
                return new ReviewedProposal(id, ProposedDeclarations.KindOf(declaration), declaration.Name, "left", proposedNode, null);
            }

            var heldNode = Node(held);
            return JsonNode.DeepEquals(proposedNode, heldNode)
                ? new ReviewedProposal(id, ProposedDeclarations.KindOf(declaration), declaration.Name, "taken", proposedNode, null)
                : new ReviewedProposal(id, ProposedDeclarations.KindOf(declaration), declaration.Name, "edited", proposedNode, heldNode);
        });

        return new AuthoringReviewView(
            loaded.Proposals.Pass,
            loaded.Proposals.Generation,
            loaded.Seen.InForce!.Version,
            loaded.Seen.Draft!.Version,
            [.. proposals]);

        static JsonNode Node(Declaration declaration) => JsonSerializer.SerializeToNode<IReadOnlyList<Declaration>>([declaration], OntologyJson.Default.IReadOnlyListDeclaration)!.AsArray()[0]!;
    }

    private static async Task<Loaded> Load(ConsoleCatalogOptions options, CancellationToken ct)
    {
        if (options.Draft.Length == 0 || options.Proposals.Length == 0)
        {
            return new Loaded
            {
                Problem = TypedResults.Problem(
                    title: "No review is set up.",
                    detail: "Saem:Console:Draft names the draft file and Saem:Console:Proposals the proposals of the pass that wrote it.",
                    statusCode: StatusCodes.Status404NotFound),
            };
        }

        ProposedDeclarations proposals;
        try
        {
            proposals = ProposedDeclarations.Parse(await File.ReadAllTextAsync(options.Proposals, ct));
        }
        catch (FileNotFoundException)
        {
            return new Loaded { Problem = TypedResults.Problem(title: "There are no proposals.", detail: "The proposals file does not exist.", statusCode: StatusCodes.Status404NotFound) };
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return new Loaded { Problem = TypedResults.Problem(title: "The proposals are refused.", detail: e.Message, statusCode: StatusCodes.Status422UnprocessableEntity) };
        }

        var seen = await CatalogConfirmApi.Compare(options, ct);
        return seen.Problem is not null ? new Loaded { Problem = seen.Problem } : new Loaded { Proposals = proposals, Seen = seen };
    }

    private static ReviewAsk? Read(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object
            || !request.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.String || draft.GetString() is not { Length: > 0 } version)
        {
            return null;
        }

        if (Ids(request, "take") is not { } take || Ids(request, "leave") is not { } leave || take.Count + leave.Count == 0)
        {
            return null;
        }

        return new ReviewAsk(version, take, leave);

        static List<string>? Ids(JsonElement owner, string name)
        {
            if (!owner.TryGetProperty(name, out var value))
            {
                return [];
            }

            if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(i => i.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(i.GetString())))
            {
                return null;
            }

            return [.. value.EnumerateArray().Select(i => i.GetString()!).Distinct(StringComparer.Ordinal)];
        }
    }

    private sealed record ReviewAsk(string Draft, List<string> Take, List<string> Leave);

    private sealed class Loaded
    {
        public IResult? Problem { get; init; }

        public ProposedDeclarations? Proposals { get; init; }

        public CatalogConfirmApi.Seen? Seen { get; init; }
    }
}

/// <summary>A pass's proposals read against the draft its review is leaving.</summary>
/// <param name="Pass">The pass's id.</param>
/// <param name="Generation">What the pass was made with, when it recorded it.</param>
/// <param name="InForce">The version of the catalog in force.</param>
/// <param name="Draft">The version of the draft — the <c>draft</c> a review names, and a confirmation after it.</param>
/// <param name="Proposals">Every proposal, in the order the pass made them.</param>
public sealed record AuthoringReviewView(string Pass, PassGeneration? Generation, string InForce, string Draft, IReadOnlyList<ReviewedProposal> Proposals);

/// <summary>One proposal and where the draft holds it.</summary>
/// <param name="Id">The kind and name, <c>entityType:Supplier</c> — what a review names.</param>
/// <param name="Kind">The declaration's kind.</param>
/// <param name="Name">The declaration's name.</param>
/// <param name="State"><c>taken</c> (the draft holds it as proposed), <c>edited</c> (it holds it changed) or <c>left</c> (it holds none).</param>
/// <param name="Declaration">The declaration as the pass proposed it.</param>
/// <param name="Current">For an edited proposal, the declaration as the draft holds it.</param>
public sealed record ReviewedProposal(string Id, string Kind, string Name, string State, JsonNode Declaration, JsonNode? Current);
