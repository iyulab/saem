using System.Text.Json.Nodes;
using Saem.Ontology;

namespace Saem.Console;

/// <summary>Where the console finds the last check of the declarations against the sources (<c>saem-materialize check --report</c>).</summary>
public sealed class ConsoleCheckOptions
{
    /// <summary>The report file; empty when this deployment keeps none.</summary>
    public string CheckReport { get; set; } = "";
}

/// <summary>
/// What is waiting at a glance (saem D-88 · PLAN slice 4): the catalog in force, how much it declares, and the last check
/// of it against the sources — run by a builder with their own account, which the console only shows. The console reads
/// no source to say any of it.
/// </summary>
internal static class OverviewApi
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/overview", async (IOntologyCatalog catalog, Microsoft.Extensions.Options.IOptions<ConsoleCheckOptions> check, CancellationToken ct) =>
        {
            Ontology.Ontology ontology;
            try
            {
                ontology = await catalog.GetAsync(ct);
            }
            catch (OntologyDeclarationException e)
            {
                return Results.Problem(title: "The catalog is refused.", detail: string.Join(" ", e.Problems), statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var report = await ReadAsync(check.Value, ct);
            return TypedResults.Ok(new Overview(
                ontology.Version,
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["entityType"] = ontology.EntityTypes.Count,
                    ["relation"] = ontology.Relations.Count,
                    ["identity"] = ontology.Identities.Count,
                    ["linkage"] = ontology.Linkages.Count,
                    ["grouping"] = ontology.Groupings.Count,
                },
                report is null ? null : Summary(report, ontology.Version)));
        });

        // The findings as the check reported them, but not the records it cites: those are rows the checking account
        // read, and a builder looking here may not be able to read them at the source (saem constitution §2).
        api.MapGet("/check", async (Microsoft.Extensions.Options.IOptions<ConsoleCheckOptions> check, CancellationToken ct) =>
            await ReadAsync(check.Value, ct) is { } report
                ? Results.Text(WithoutRecords(report).ToJsonString(), "application/json")
                : Results.Problem(title: "No check report.", detail: "Run saem-materialize check --report <file> and name the file in Saem:Console:CheckReport.", statusCode: StatusCodes.Status404NotFound))
            .RequireAuthorization(ConsoleRoles.Builder);
    }

    private static async Task<JsonObject?> ReadAsync(ConsoleCheckOptions options, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.CheckReport) || !File.Exists(options.CheckReport))
        {
            return null;
        }

        await using var file = File.OpenRead(options.CheckReport);
        return await JsonNode.ParseAsync(file, cancellationToken: ct) as JsonObject;
    }

    internal static CheckSummary Summary(JsonObject report, string catalogInForce)
    {
        var findings = report["findings"]?.AsArray() ?? [];
        var checkedVersion = report["catalogVersion"]?.GetValue<string>();
        return new CheckSummary(
            report["checkedAt"]?.GetValue<DateTimeOffset>(),
            checkedVersion,
            checkedVersion == catalogInForce,
            report["holds"]?.GetValue<bool>() ?? false,
            findings.Count(f => f?["violation"]?.GetValue<bool>() == true));
    }

    internal static JsonObject WithoutRecords(JsonObject report)
    {
        var shown = (JsonObject)report.DeepClone();
        foreach (var finding in shown["findings"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            finding["recordsCited"] = finding["records"]?.AsArray().Count ?? 0;
            finding.Remove("records");
        }

        return shown;
    }
}

/// <summary>The console's overview.</summary>
/// <param name="CatalogVersion">The catalog in force.</param>
/// <param name="Declarations">How many declarations of each kind it holds.</param>
/// <param name="Check">The last check against the sources, or null when the deployment keeps no report.</param>
public sealed record Overview(string CatalogVersion, IReadOnlyDictionary<string, int> Declarations, CheckSummary? Check);

/// <summary>The last check, in brief.</summary>
/// <param name="CheckedAt">When it ran.</param>
/// <param name="CatalogVersion">The catalog it checked.</param>
/// <param name="Current">Whether that is the catalog in force — a check of an older one says nothing about this one.</param>
/// <param name="Holds">Whether every declaration held (with a change, whether none got worse).</param>
/// <param name="Violations">How many findings say a declaration does not hold.</param>
public sealed record CheckSummary(DateTimeOffset? CheckedAt, string? CatalogVersion, bool Current, bool Holds, int Violations);
