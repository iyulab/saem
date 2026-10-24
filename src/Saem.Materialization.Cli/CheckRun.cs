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
          saem-materialize check --catalog <file> --sources <file>

          --catalog <file>               The ontology declarations to check.
          --sources <file>               As for a pass: system -> how the account reads it. Its secrets come from
                                         SAEM_SOURCE_SECRET_<SYSTEM>.

        Every declared table is read whole as that account: whether it and its columns are there, whether a key
        names one row, whether a number key spells an integer, whether the record columns are filled, and how many
        of each relation's values name an entity of the type it leads to. Findings cite up to five records by key —
        source values: keep the report as you keep the server's logs. Exit status 1 when a declaration does not hold.
        """;

    /// <summary>The catalog and sources files a check is told.</summary>
    /// <exception cref="MaterializeUsageException">The command line is incomplete or malformed.</exception>
    public static (string Catalog, string Sources) Parse(IReadOnlyList<string> args)
    {
        string? catalog = null, sources = null;
        for (var i = 0; i < args.Count; i += 2)
        {
            if (args[i] is not ("--catalog" or "--sources"))
            {
                throw new MaterializeUsageException($"Unknown argument '{args[i]}'.");
            }

            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new MaterializeUsageException($"{args[i]} needs a value.");
            }

            if (args[i] == "--catalog")
            {
                catalog = args[i + 1];
            }
            else
            {
                sources = args[i + 1];
            }
        }

        return (catalog ?? throw new MaterializeUsageException("--catalog names the declarations to check."),
            sources ?? throw new MaterializeUsageException("--sources names how the account reads each system."));
    }

    /// <summary>Runs the check and writes its findings; true when every declaration holds.</summary>
    public static async Task<bool> RunAsync(Ontology.Ontology ontology, IReadOnlyDictionary<string, SourceOpener> openers, TextWriter output, CancellationToken cancellationToken)
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
        return violations == 0;
    }
}
