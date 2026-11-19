using System.Globalization;
using Eyu.Core.Ports;
using Eyu.Core.Primitives;
using Saem.Authoring.Shadow;
using Saem.Connectors.Formbase;
using Saem.Ontology;

namespace Saem.Authoring.Cli;

/// <summary>What one <c>saem-author shadow</c> is told: the pass's proposals, the Formbase to look in again, and where the findings are kept.</summary>
internal sealed record ShadowOptions(
    Uri Formbase, string? FormbaseNamespace, string? FormbaseToken, string ProposalsPath, string LedgerPath, RepeatOptions? Repeat = null)
{
    public const string Usage = """
        saem-author shadow — look again, in the source, at the records a pass's proposals were drawn from.

        Usage:
          saem-author shadow --proposals <file> --formbase <url> [options]

          --proposals <file>             The proposals of the pass to look at (written by saem-author).
          --formbase <url>               The Formbase instance the records were sampled from.
          --formbase-namespace <name>    The Formbase namespace to read; omitted, the instance's own.
          --ledger <path>                Where the findings are appended (default shadow-judgments.jsonl beside the proposals).

        Also ask a model the same question about each proposal several times and record whether it says the same thing (it can be
        steadily wrong; this finds only whether it wavers):
          --repeat-consistency           Turn this on; it needs the next two.
          --model-endpoint <url>         An OpenAI-compatible base address, version segment included (…/v1/).
          --model <name>                 The model to ask.
          --repeats <n>                  How many times each proposal is asked about (default 5).
          --prompt-version <1|2>         1 shows a relation's own records only; 2 (default) also shows the records of the type it points to.
          --model-stream                 Ask for the answer as it is written (behind a gateway that cuts a slow first byte).
          --model-timeout-minutes <n>    How long to wait on the model before a call fails (default 10).
          --retries <n>                  How many more times a call that fails transiently is tried (default 1).
        At most 10 proposals are asked about; a pass with more is stopped before any call. A model call takes minutes.

        For each entity type and relation the pass proposed, each cited document is read again from its form type's
        stream: still the current version of its record, corrected since, retired, or gone — and whether the
        field the proposal depends on is carried. No model is asked and nothing but the ledger is written; the
        review, the draft and the catalog are not read or changed, and nothing reads the ledger back into them.
        Delete the ledger and nothing else is different. Run it again later: each run appends its own findings.

        Environment:
          SAEM_FORMBASE_TOKEN            One of Formbase's access keys, sent as a bearer token.
          SAEM_MODEL_API_KEY             A bearer token sent to the model endpoint.
        """;

    /// <exception cref="AuthorUsageException">The command line is incomplete or malformed.</exception>
    public static ShadowOptions Parse(IReadOnlyList<string> args, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "--repeat-consistency" or "--model-stream")
            {
                if (!values.TryAdd(arg, "true"))
                {
                    throw new AuthorUsageException($"{arg} is given more than once.");
                }

                continue;
            }

            if (arg is not ("--proposals" or "--formbase" or "--formbase-namespace" or "--ledger" or "--model-endpoint" or "--model" or "--repeats" or "--prompt-version" or "--model-timeout-minutes" or "--retries"))
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

        var proposals = values.TryGetValue("--proposals", out var p) ? p : throw new AuthorUsageException("--proposals is required.");
        var formbase = values.TryGetValue("--formbase", out var f) ? f : throw new AuthorUsageException("--formbase is required.");
        RepeatOptions? repeat = null;
        if (values.ContainsKey("--repeat-consistency"))
        {
            var endpoint = values.TryGetValue("--model-endpoint", out var e) ? e : throw new AuthorUsageException("--repeat-consistency needs --model-endpoint.");
            repeat = new RepeatOptions(
                Uri.TryCreate(endpoint.EndsWith('/') ? endpoint : endpoint + "/", UriKind.Absolute, out var model) && model.Scheme is "http" or "https"
                    ? model
                    : throw new AuthorUsageException($"--model-endpoint takes an http(s) address, not '{endpoint}'."),
                values.TryGetValue("--model", out var name) ? name : throw new AuthorUsageException("--repeat-consistency needs --model."),
                environment("SAEM_MODEL_API_KEY") is { Length: > 0 } key ? key : null,
                values.ContainsKey("--model-stream"),
                TimeSpan.FromMinutes(Count("--model-timeout-minutes", 10, 1)),
                Count("--retries", 1, 0),
                Count("--repeats", RepeatConsistency.DefaultRepeats, 2),
                Count("--prompt-version", RepeatConsistency.Version, 1));
        }
        else if (values.Keys.Any(k => k is "--model-endpoint" or "--model" or "--repeats" or "--prompt-version" or "--model-timeout-minutes" or "--retries" or "--model-stream"))
        {
            throw new AuthorUsageException("The model options belong to --repeat-consistency, which is not given.");
        }

        int Count(string name, int fallback, int minimum) =>
            !values.TryGetValue(name, out var text) ? fallback
            : int.TryParse(text, System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= minimum ? number
            : throw new AuthorUsageException($"{name} takes a whole number of at least {minimum}, not '{text}'.");

        return new ShadowOptions(
            Uri.TryCreate(formbase.EndsWith('/') ? formbase : formbase + "/", UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                ? uri
                : throw new AuthorUsageException($"--formbase takes an http(s) address, not '{formbase}'."),
            values.GetValueOrDefault("--formbase-namespace"),
            environment(AuthorOptions.FormbaseTokenVariable) is { Length: > 0 } token ? token : null,
            proposals,
            values.GetValueOrDefault("--ledger") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(proposals)) ?? ".", "shadow-judgments.jsonl"),
            repeat);
    }
}

/// <summary>The model a repeated judgment asks, and how many times it asks each proposal.</summary>
internal sealed record RepeatOptions(Uri Endpoint, string Model, string? ApiKey, bool Stream, TimeSpan Timeout, int Retries, int Repeats, int PromptVersion = RepeatConsistency.Version);

/// <summary>The Formbase connector as the place cited documents are read again.</summary>
internal sealed class FormbaseCitationSource(FormbaseConnector connector) : ICitationSource
{
    public async Task<IReadOnlyDictionary<string, CitedReading>> ReadAsync(string subject, IReadOnlyCollection<string> recordIds, CancellationToken cancellationToken)
    {
        var read = await connector.ReadCitedAsync(SubjectRef.Create(subject), recordIds, cancellationToken).ConfigureAwait(false);
        return read.ToDictionary(r => r.Key, r => new CitedReading(Standing(r.Value.Standing), r.Value.Fields), StringComparer.Ordinal);
    }

    private static CitedStanding Standing(DocumentStanding standing) => standing switch
    {
        DocumentStanding.Standing => CitedStanding.Standing,
        DocumentStanding.Superseded => CitedStanding.Superseded,
        DocumentStanding.Retired => CitedStanding.Retired,
        DocumentStanding.Missing => CitedStanding.Missing,
        DocumentStanding.NotCheckable => CitedStanding.NotCheckable,
        _ => throw new ArgumentOutOfRangeException(nameof(standing)),
    };
}

/// <summary>Looks again at what a pass's proposals cite and appends what it finds to the ledger.</summary>
internal static class ShadowRun
{
    /// <exception cref="AuthorRunException">The proposals cannot be read.</exception>
    public static async Task RunAsync(
        ShadowOptions options, ICitationSource source, DateTimeOffset at, TextWriter output, CancellationToken cancellationToken, IModelClient? model = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(output);

        ProposedDeclarations proposals;
        try
        {
            proposals = ProposedDeclarations.Parse(await File.ReadAllTextAsync(options.ProposalsPath, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            throw new AuthorRunException($"The proposals at '{options.ProposalsPath}' cannot be read: {e.Message}");
        }

        if (proposals.Evidence is null)
        {
            await output.WriteLineAsync($"Pass {proposals.Pass} kept no record of what its proposals were drawn from (it was written before they were kept); there is nothing to look at again.").ConfigureAwait(false);
            return;
        }

        if (options.Repeat is not null && model is null)
        {
            throw new AuthorRunException("--repeat-consistency was given and no model to ask was.");
        }

        // The model is asked after the source is looked at, and the ledger takes what the first found before the second begins:
        // a run of minutes that is stopped keeps what it had.
        var result = await SourceRecheck.JudgeAsync(proposals, source, at, cancellationToken).ConfigureAwait(false);
        await ShadowLedger.AppendAsync(options.LedgerPath, result.Rows, cancellationToken).ConfigureAwait(false);

        var counts = result.Rows.GroupBy(r => r.Verdict).ToDictionary(g => g.Key, g => g.Count());
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Pass {proposals.Pass}: {result.Rows.Count} proposals looked at — {counts.GetValueOrDefault(ShadowVerdict.Supported)} supported, " +
            $"{counts.GetValueOrDefault(ShadowVerdict.Unsupported)} unsupported, {counts.GetValueOrDefault(ShadowVerdict.Unreadable)} unreadable; " +
            $"{result.NotJudged.Count} had no citations kept. Ledger: {options.LedgerPath}")).ConfigureAwait(false);
        foreach (var row in result.Rows.Where(r => r.Verdict != ShadowVerdict.Supported))
        {
            await output.WriteLineAsync($"  {row.Verdict,-11} {row.ItemId}: " +
                string.Join(", ", row.Cites.GroupBy(c => c.Standing).Select(g => $"{g.Count()} {g.Key}".ToLowerInvariant())) +
                (row.FieldCarried == false ? $"; none carries '{row.Field}'" : "") +
                (row.Note is null ? "" : $" — {row.Note}")).ConfigureAwait(false);
        }

        if (options.Repeat is { } repeat)
        {
            await output.WriteLineAsync($"Asking {repeat.Model} {repeat.Repeats} times about each proposal (prompt version {repeat.PromptVersion}, {RepeatConsistency.FingerprintOf(repeat.PromptVersion)}); a call takes minutes.").ConfigureAwait(false);
            var lines = new List<string>();
            var asked = await RepeatConsistency.JudgeAsync(proposals, source, model!, repeat.Repeats, DateTimeOffset.UtcNow, repeat.PromptVersion, lines.Add, cancellationToken).ConfigureAwait(false);
            foreach (var line in lines)
            {
                await output.WriteLineAsync("  " + line).ConfigureAwait(false);
            }

            await ShadowLedger.AppendAsync(options.LedgerPath, asked.Rows, cancellationToken).ConfigureAwait(false);
            var tally = asked.Rows.GroupBy(r => r.Verdict.StartsWith(ShadowVerdict.StablePrefix, StringComparison.Ordinal) ? "stable" : r.Verdict).ToDictionary(g => g.Key, g => g.Count());
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"Repeat consistency: {asked.Rows.Count} proposals — {tally.GetValueOrDefault("stable")} stable, {tally.GetValueOrDefault(ShadowVerdict.Wobbling)} wobbling, {tally.GetValueOrDefault(ShadowVerdict.Incomplete)} incomplete.")).ConfigureAwait(false);
            foreach (var row in asked.Rows)
            {
                await output.WriteLineAsync($"  {row.ItemId}: {row.Verdict} [{string.Join(", ", row.Votes ?? [])}]").ConfigureAwait(false);
            }
        }
    }
}
