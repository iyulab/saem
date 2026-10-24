using System.Globalization;
using Saem.Ontology;

namespace Saem.Authoring.Cli;

/// <summary>What one <c>saem-author diff</c> is told: the catalog in force and the one about to replace it.</summary>
internal sealed record DiffOptions(string BeforePath, string AfterPath)
{
    public const string Usage = """
        saem-author diff — sort a catalog change into what only adds vocabulary and what can break.

        Usage:
          saem-author diff --before <file> --after <file>

          --before <file>   The ontology declarations in force.
          --after <file>    The declarations about to replace them.

        Breaking: a type, relation, field, inverse, identity or grouping member taken away, or one that now reads
        another table, column or key, compares its keys otherwise, cites its records otherwise, or tells what is one
        thing otherwise — a call that worked may now be refused, or answer from somewhere else. A declaration renamed
        reads as one removed and one added. Additive: everything new. Exit status 1 when any change breaks, so a
        deployment can hold such a catalog for review; 2 when a catalog cannot be read or is not consistent.
        """;

    /// <exception cref="AuthorUsageException">The command line is incomplete or malformed.</exception>
    public static DiffOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? before = null, after = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is not ("--before" or "--after"))
            {
                throw new AuthorUsageException($"Unknown argument '{arg}'.");
            }

            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new AuthorUsageException($"{arg} needs a value.");
            }

            ref var value = ref arg == "--before" ? ref before : ref after;
            if (value is not null)
            {
                throw new AuthorUsageException($"{arg} is given more than once.");
            }

            value = args[++i];
        }

        return new DiffOptions(
            before ?? throw new AuthorUsageException("--before is required."),
            after ?? throw new AuthorUsageException("--after is required."));
    }
}

/// <summary>Reads two catalogs and writes the changes between them, breaking ones first.</summary>
internal static class DiffRun
{
    /// <summary>Whether no change breaks.</summary>
    /// <exception cref="OntologyDeclarationException">A catalog cannot be read, or is not consistent.</exception>
    public static async Task<bool> RunAsync(DiffOptions options, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        var before = await new FileOntologyCatalog(options.BeforePath).GetAsync(cancellationToken).ConfigureAwait(false);
        var after = await new FileOntologyCatalog(options.AfterPath).GetAsync(cancellationToken).ConfigureAwait(false);
        var changes = OntologyChanges.Between(before, after);
        var breaking = changes.Count(c => c.Kind == ChangeKind.Breaking);
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"catalog {before.Version} -> {after.Version}: {breaking} breaking, {changes.Count - breaking} additive.")).ConfigureAwait(false);
        foreach (var change in changes)
        {
            await output.WriteLineAsync($"  {(change.Kind == ChangeKind.Breaking ? "breaking" : "additive")}  {change.Declaration}: {change.Change}").ConfigureAwait(false);
        }

        return breaking == 0;
    }
}
