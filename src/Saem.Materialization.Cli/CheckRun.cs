using System.Globalization;
using System.Text.Json;
using Saem.Ontology;
using Saem.Query;

namespace Saem.Materialization.Cli;

/// <summary>
/// <c>saem-materialize check</c>: the declarations checked against the sources, read as the account the sources
/// file names — a builder's own, so the findings hold for the rows that account may read. Nothing is written.
/// </summary>
internal static class CheckRun
{
    public const string Usage = """
        saem-materialize check — check the declarations against the sources, as one account.

        Usage:
          saem-materialize check --catalog <file> --sources <file> [--before <file>] [--report <file>]

          --catalog <file>               The ontology declarations to check.
          --before <file>                The declarations the catalog is to replace: checked against the same sources,
                                         and what changes between the two is reported — a change simulated before it
                                         is put in force.
          --sources <file>               As for a pass: system -> how the account reads it. Its secrets come from
                                         SAEM_SOURCE_SECRET_<SYSTEM>.
          --report <file>                Also write the findings (and, with --before, the differences) as JSON — the
                                         file the administration console shows.

        Every declared table is read whole as that account: whether it and its columns are there, whether a key
        names one row, whether a number key spells an integer, whether the record columns are filled, and how many
        of each relation's values name an entity of the type it leads to. Findings cite up to five records by key —
        source values: keep the report as you keep the server's logs. Exit status 1 when a declaration does not hold —
        with --before, when the change makes one hold for fewer rows than it did.
        """;

    /// <summary>The catalog, sources and (when given) before and report files a check is told.</summary>
    /// <exception cref="MaterializeUsageException">The command line is incomplete or malformed.</exception>
    public static (string Catalog, string Sources, string? Before, string? Report) Parse(IReadOnlyList<string> args)
    {
        string? catalog = null, sources = null, before = null, report = null;
        for (var i = 0; i < args.Count; i += 2)
        {
            if (args[i] is not ("--catalog" or "--sources" or "--before" or "--report"))
            {
                throw new MaterializeUsageException($"Unknown argument '{args[i]}'.");
            }

            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new MaterializeUsageException($"{args[i]} needs a value.");
            }

            switch (args[i])
            {
                case "--catalog":
                    catalog = args[i + 1];
                    break;
                case "--sources":
                    sources = args[i + 1];
                    break;
                case "--report":
                    report = args[i + 1];
                    break;
                default:
                    before = args[i + 1];
                    break;
            }
        }

        return (catalog ?? throw new MaterializeUsageException("--catalog names the declarations to check."),
            sources ?? throw new MaterializeUsageException("--sources names how the account reads each system."),
            before, report);
    }

    /// <summary>
    /// Runs the check and writes its findings; true when every declaration holds. With <paramref name="before"/>, the
    /// declarations it is to replace are checked against the same sources too, what changes is written after the
    /// findings, and true means the change makes no declaration hold for fewer rows. With <paramref name="report"/>,
    /// the same is written there as JSON (<see cref="WriteReport"/>).
    /// </summary>
    public static async Task<bool> RunAsync(
        Ontology.Ontology ontology, IReadOnlyDictionary<string, SourceOpener> openers, TextWriter output, CancellationToken cancellationToken,
        Ontology.Ontology? before = null, Stream? report = null, TimeProvider? time = null)
    {
        var checkedAt = (time ?? TimeProvider.System).GetUtcNow();
        var findings = await new DeclarationCheck(ontology).RunAsync(new PersonSessions(openers), cancellationToken).ConfigureAwait(false);
        foreach (var finding in findings.Findings)
        {
            var subject = finding.Relation is null ? finding.Type : $"{finding.Type}.{finding.Relation}";
            var cited = finding.Records.Count > 0 ? $" — e.g. {string.Join(", ", finding.Records)}" : "";
            await output.WriteLineAsync($"{(finding.Kind is "read" or "resolved" ? "  ok  " : "  FAIL")} {subject} [{finding.Kind}] {finding.What}{cited}").ConfigureAwait(false);
        }

        var violations = findings.Violations.Count;
        await output.WriteLineAsync(violations == 0
            ? "Every declaration holds for the rows this account reads."
            : $"{violations} declaration finding(s) do not hold for the rows this account reads.").ConfigureAwait(false);
        if (before is null)
        {
            if (report is not null)
            {
                WriteReport(report, ontology, checkedAt, findings, null, null);
            }

            return violations == 0;
        }

        var differences = findings.Against(await new DeclarationCheck(before).RunAsync(new PersonSessions(openers), cancellationToken).ConfigureAwait(false));
        if (report is not null)
        {
            WriteReport(report, ontology, checkedAt, findings, before, differences);
        }

        await output.WriteLineAsync("Against the declarations it replaces:").ConfigureAwait(false);
        foreach (var difference in differences)
        {
            var subject = difference.Relation is null ? difference.Type : $"{difference.Type}.{difference.Relation}";
            var mark = difference.IsWorse ? "  WORSE" : "       ";
            await output.WriteLineAsync($"{mark} {subject} [{difference.Kind}] {difference.Before?.ToString(CultureInfo.InvariantCulture) ?? "-"} -> {difference.After?.ToString(CultureInfo.InvariantCulture) ?? "-"}").ConfigureAwait(false);
        }

        var worse = differences.Count(d => d.IsWorse);
        await output.WriteLineAsync(differences.Count == 0
            ? "The change finds the same against the sources."
            : worse == 0
                ? "The change makes no declaration hold for fewer rows."
                : $"{worse} finding(s) are worse with the change.").ConfigureAwait(false);
        return worse == 0;
    }

    /// <summary>
    /// The check as JSON: <c>{ "checkedAt", "catalogVersion", "holds", "findings": [ { "type", "relation", "kind",
    /// "violation", "what", "count", "records" } ], "against": { "catalogVersion", "worse", "differences": [ { "type",
    /// "relation", "kind", "before", "after", "worse" } ] } }</c> — <c>against</c> only with the declarations replaced.
    /// <c>records</c> are source keys, cited as the text report cites them.
    /// </summary>
    internal static void WriteReport(
        Stream report, Ontology.Ontology ontology, DateTimeOffset checkedAt, CheckReport findings, Ontology.Ontology? before, IReadOnlyList<CheckDifference>? differences)
    {
        using var json = new Utf8JsonWriter(report, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("checkedAt", checkedAt);
        json.WriteString("catalogVersion", ontology.Version);
        json.WriteBoolean("holds", differences is null ? findings.Violations.Count == 0 : !differences.Any(d => d.IsWorse));
        json.WriteStartArray("findings");
        foreach (var f in findings.Findings)
        {
            json.WriteStartObject();
            json.WriteString("type", f.Type);
            json.WriteString("relation", f.Relation);
            json.WriteString("kind", f.Kind);
            json.WriteBoolean("violation", f.IsViolation);
            json.WriteString("what", f.What);
            json.WriteNumber("count", f.Count);
            json.WriteStartArray("records");
            foreach (var record in f.Records)
            {
                json.WriteStringValue(record);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteEndArray();
        if (before is not null && differences is not null)
        {
            json.WriteStartObject("against");
            json.WriteString("catalogVersion", before.Version);
            json.WriteNumber("worse", differences.Count(d => d.IsWorse));
            json.WriteStartArray("differences");
            foreach (var d in differences)
            {
                json.WriteStartObject();
                json.WriteString("type", d.Type);
                json.WriteString("relation", d.Relation);
                json.WriteString("kind", d.Kind);
                WriteCount(json, "before", d.Before);
                WriteCount(json, "after", d.After);
                json.WriteBoolean("worse", d.IsWorse);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteEndObject();
    }

    private static void WriteCount(Utf8JsonWriter json, string name, int? count)
    {
        if (count is { } value)
        {
            json.WriteNumber(name, value);
        }
        else
        {
            json.WriteNull(name);
        }
    }
}
