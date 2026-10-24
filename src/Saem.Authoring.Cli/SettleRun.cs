using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Saem.Authoring.Cli;

/// <summary>
/// What one <c>saem-author settle</c> is told: the pass's proposals, the catalog the review left in force,
/// the settled records to add to, and who settled it.
/// </summary>
internal sealed record SettleOptions(string ProposalsPath, string CatalogPath, string RecordsPath, string? SettledBy)
{
    public const string Usage = """
        saem-author settle — record what a review took of an authoring pass's proposals.

        Usage:
          saem-author settle --proposals <file> --catalog <file> --records <file> [--settled-by <id>]

          --proposals <file>   The proposals the pass wrote (saem-author --proposals).
          --catalog <file>     The ontology declarations in force once the review is done.
          --records <file>     The settled records, one JSON line each, in the form Gil reads; the pass's lines
                               are added to the end. A pass already recorded there is refused.
          --settled-by <id>    An opaque id of the person who reviewed — never a name. Omitted, it is not kept.

        Run it when the review is in the catalog: the time it runs is the time the lines say it was settled.
        """;

    /// <exception cref="AuthorUsageException">The command line is incomplete or malformed.</exception>
    public static SettleOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is not ("--proposals" or "--catalog" or "--records" or "--settled-by"))
            {
                throw new AuthorUsageException($"Unknown argument '{arg}'.");
            }

            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new AuthorUsageException($"{arg} needs a value.");
            }

            if (!values.TryAdd(arg, args[++i]))
            {
                throw new AuthorUsageException($"{arg} is given more than once.");
            }
        }

        return new SettleOptions(Required("--proposals"), Required("--catalog"), Required("--records"), values.GetValueOrDefault("--settled-by"));

        string Required(string name) =>
            values.TryGetValue(name, out var value) ? value : throw new AuthorUsageException($"{name} is required.");
    }
}

/// <summary>Reads a pass's proposals and the catalog in force, and adds the pass's settled records.</summary>
internal static class SettleRun
{
    /// <exception cref="AuthorRunException">An input cannot be read, or the pass is already recorded.</exception>
    public static async Task RunAsync(SettleOptions options, DateTimeOffset settledAt, TextWriter log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        ProposedDeclarations proposed;
        try
        {
            proposed = ProposedDeclarations.Parse(await File.ReadAllTextAsync(options.ProposalsPath, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            throw new AuthorRunException($"The proposals '{options.ProposalsPath}' cannot be read: {e.Message}", e);
        }

        if (!File.Exists(options.CatalogPath))
        {
            throw new AuthorRunException($"The catalog '{options.CatalogPath}' does not exist; settle reads the catalog the review left in force.");
        }

        var inForce = await AuthorRun.ReadCatalogAsync(options.CatalogPath, cancellationToken).ConfigureAwait(false);
        if (await RecordsPassAsync(options.RecordsPath, proposed.Pass, cancellationToken).ConfigureAwait(false))
        {
            throw new AuthorRunException($"The pass '{proposed.Pass}' is already recorded in '{options.RecordsPath}'.");
        }

        var lines = ReviewSettlement.Lines(proposed, inForce, settledAt, options.SettledBy);
        var text = new StringBuilder();
        var taken = 0;
        foreach (var line in lines)
        {
            text.Append(line).Append('\n');
            using var record = JsonDocument.Parse(line);
            taken += record.RootElement.GetProperty("values").GetProperty("taken").GetString() == "yes" ? 1 : 0;
        }

        await File.AppendAllTextAsync(options.RecordsPath, text.ToString(), cancellationToken).ConfigureAwait(false);
        await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"{lines.Count} proposals of {proposed.Pass} settled ({taken} taken) and added to {options.RecordsPath}.")).ConfigureAwait(false);
    }

    private static async Task<bool> RecordsPassAsync(string path, string pass, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                using var record = JsonDocument.Parse(line);
                if (record.RootElement.TryGetProperty("batch", out var batch) && batch.ValueKind == JsonValueKind.String && batch.GetString() == pass)
                {
                    return true;
                }
            }
            catch (JsonException e)
            {
                throw new AuthorRunException($"The settled records '{path}' hold a line that is not JSON: {e.Message}", e);
            }
        }

        return false;
    }
}
