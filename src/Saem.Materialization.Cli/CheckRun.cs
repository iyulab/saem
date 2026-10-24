using System.Globalization;
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
          saem-materialize check --catalog <file> --sources <file> [--before <file>]

          --catalog <file>               The ontology declarations to check.
          --before <file>                The declarations the catalog is to replace: checked against the same sources,
                                         and what changes between the two is reported — a change simulated before it
                                         is put in force.
          --sources <file>               As for a pass: system -> how the account reads it. Its secrets come from
                                         SAEM_SOURCE_SECRET_<SYSTEM>.

        Every declared table is read whole as that account: whether it and its columns are there, whether a key
        names one row, whether a number key spells an integer, whether the record columns are filled, and how many
        of each relation's values name an entity of the type it leads to. Findings cite up to five records by key —
        source values: keep the report as you keep the server's logs. Exit status 1 when a declaration does not hold —
        with --before, when the change makes one hold for fewer rows than it did.
        """;

    /// <summary>The catalog, sources and (when given) before files a check is told.</summary>
    /// <exception cref="MaterializeUsageException">The command line is incomplete or malformed.</exception>
    public static (string Catalog, string Sources, string? Before) Parse(IReadOnlyList<string> args)
    {
        string? catalog = null, sources = null, before = null;
        for (var i = 0; i < args.Count; i += 2)
        {
            if (args[i] is not ("--catalog" or "--sources" or "--before"))
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
                default:
                    before = args[i + 1];
                    break;
            }
        }

        return (catalog ?? throw new MaterializeUsageException("--catalog names the declarations to check."),
            sources ?? throw new MaterializeUsageException("--sources names how the account reads each system."),
            before);
    }

    /// <summary>
    /// Runs the check and writes its findings; true when every declaration holds. With <paramref name="before"/>, the
    /// declarations it is to replace are checked against the same sources too, what changes is written after the
    /// findings, and true means the change makes no declaration hold for fewer rows.
    /// </summary>
    public static async Task<bool> RunAsync(
        Ontology.Ontology ontology, IReadOnlyDictionary<string, SourceOpener> openers, TextWriter output, CancellationToken cancellationToken, Ontology.Ontology? before = null)
    {
        var report = await new DeclarationCheck(ontology).RunAsync(new PersonSessions(openers), cancellationToken).ConfigureAwait(false);
        foreach (var finding in report.Findings)
        {
            var subject = finding.Relation is null ? finding.Type : $"{finding.Type}.{finding.Relation}";
            var cited = finding.Records.Count > 0 ? $" — e.g. {string.Join(", ", finding.Records)}" : "";
            await output.WriteLineAsync($"{(finding.Kind is "read" or "resolved" ? "  ok  " : "  FAIL")} {subject} [{finding.Kind}] {finding.What}{cited}").ConfigureAwait(false);
        }

        var violations = report.Violations.Count;
        await output.WriteLineAsync(violations == 0
            ? "Every declaration holds for the rows this account reads."
            : $"{violations} declaration finding(s) do not hold for the rows this account reads.").ConfigureAwait(false);
        if (before is null)
        {
            return violations == 0;
        }

        var differences = report.Against(await new DeclarationCheck(before).RunAsync(new PersonSessions(openers), cancellationToken).ConfigureAwait(false));
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
}
