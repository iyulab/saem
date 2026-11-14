using System.Globalization;
using System.Text;
using System.Text.Json;
using Eyu.Core.Inference;
using Eyu.Core.Ports;
using Saem.Ontology;

namespace Saem.Authoring.Cli;

/// <summary>
/// One authoring pass from files to files: the sources and the declarations in force are read, the pass
/// runs, and the review, the draft and the pass's proposals are written — nothing else. Both outputs are checked before anything
/// is read or any model is asked, so a run that would replace a file it was not told to replace costs nothing.
/// </summary>
/// <summary>What the proposer is: its library version and the fingerprints of the prompt wording it was built with.</summary>
/// <param name="Version">The proposer library's version.</param>
/// <param name="PromptFingerprint">The fingerprint of the prompt's fixed wording.</param>
/// <param name="KnownTypesPromptFingerprint">The fingerprint of the wording added when the pass names types in use.</param>
internal sealed record ProposerFacts(string Version, string PromptFingerprint, string KnownTypesPromptFingerprint);

internal static class AuthorRun
{
    private static readonly JsonSerializerOptions SourcesJson = new(JsonSerializerDefaults.Web);

    /// <param name="options">What the run was told.</param>
    /// <param name="proposer">Eyu's proposer, over the model the options name.</param>
    /// <param name="records">Where the subjects' documents are sampled from.</param>
    /// <param name="structures">Where the subjects' declarations are read from.</param>
    /// <param name="retries">The model calls retried during the pass, filled as the pass runs; the review reports them.</param>
    /// <param name="log">Where progress is written.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <param name="proposerFacts">What the proposer is, for the record of what the pass was made with; omitted, the pass records none.</param>
    /// <exception cref="AuthorRunException">An input cannot be read, or an output exists and the run was not told to replace it.</exception>
    public static async Task RunAsync(
        AuthorOptions options,
        IOntologyProposer proposer,
        IRecordSample records,
        IStructureSource structures,
        IReadOnlyCollection<ModelCallRetry> retries,
        TextWriter log,
        CancellationToken cancellationToken,
        ProposerFacts? proposerFacts = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(retries);
        ArgumentNullException.ThrowIfNull(log);

        // A proposal has no effect until a person adopts it: the draft goes beside the catalog in force, not over it,
        // unless the caller says in so many words that the catalog's own diff is the review.
        if (!options.ReplaceCatalog && SameFile(options.DraftPath, options.CatalogPath))
        {
            throw new AuthorRunException(
                $"'{options.DraftPath}' is the catalog in force; a draft written there is in force before anyone has reviewed it. "
                + "Point --draft at another file, or pass --replace-catalog (with --overwrite) when the catalog is under version control and its diff is the review.");
        }

        foreach (var output in new[] { options.ReviewPath, options.DraftPath, options.ProposalsPath })
        {
            if (!options.Overwrite && File.Exists(output))
            {
                throw new AuthorRunException($"'{output}' exists; pass --overwrite to replace it.");
            }
        }

        var ranAt = DateTimeOffset.UtcNow;
        var pass = ReviewSettlement.PassId(ranAt);
        var sources = await ReadSourcesAsync(options.SourcesPath, cancellationToken).ConfigureAwait(false);
        var existing = await ReadCatalogAsync(options.CatalogPath, cancellationToken).ConfigureAwait(false);
        await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Sampling {sources.Count} subjects ({options.RecordsPerSubject} documents each, {options.Sample.ToString().ToLowerInvariant()} first) against {existing.Count} declarations in force; asking {options.Model}.")).ConfigureAwait(false);

        var drafted = await new DeclarationAuthoring(proposer, records, structures)
            .DraftAsync(sources, existing, options.RecordsPerSubject, cancellationToken).ConfigureAwait(false);

        var generation = proposerFacts is null
            ? null
            : new PassGeneration(
                options.Model,
                proposerFacts.Version,
                proposerFacts.PromptFingerprint,
                existing.OfType<EntityTypeDeclaration>().Any() ? proposerFacts.KnownTypesPromptFingerprint : null,
                ranAt,
                retries.Count);
        await File.WriteAllTextAsync(options.ReviewPath, drafted.ToReview() + (generation?.ToReviewSection() ?? "") + RetriesSection(retries), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(options.DraftPath, drafted.Draft.ToJson(), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(options.ProposalsPath, new ProposedDeclarations(pass, drafted.Draft.Added, generation).ToJson(), cancellationToken).ConfigureAwait(false);
        await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"{drafted.Draft.Added.Count} declarations drafted, {drafted.Draft.Notes.Count} not declared, {retries.Count} model calls retried. " +
            $"Review: {options.ReviewPath} · draft: {options.DraftPath} · proposals: {options.ProposalsPath}")).ConfigureAwait(false);
    }

    /// <summary>
    /// The retried calls as a section of the review, so a draft drawn from a second attempt says so; empty
    /// when every call went through the first time.
    /// </summary>
    internal static string RetriesSection(IReadOnlyCollection<ModelCallRetry> retries)
    {
        if (retries.Count == 0)
        {
            return "";
        }

        var text = new StringBuilder();
        text.AppendLine().AppendLine("## Model calls retried").AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"{retries.Count} model call attempts failed transiently and were tried again; the draft is drawn from the answers that came through.").AppendLine();
        foreach (var retry in retries)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- attempt {retry.Attempt}: {retry.Failure.GetType().Name}: {retry.Failure.Message}");
        }

        return text.ToString();
    }

    /// <summary>Whether two paths name the same file — compared case-insensitively, so a case-folding file system cannot be used to slip past.</summary>
    private static bool SameFile(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static async Task<IReadOnlyDictionary<string, SubjectSource>> ReadSourcesAsync(string path, CancellationToken cancellationToken)
    {
        Dictionary<string, SubjectSource>? sources;
        try
        {
            var stream = File.OpenRead(path);
            await using (stream.ConfigureAwait(false))
            {
                sources = await JsonSerializer.DeserializeAsync<Dictionary<string, SubjectSource>>(stream, SourcesJson, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            throw new AuthorRunException($"The sources file '{path}' cannot be read: {e.Message}", e);
        }

        if (sources is null || sources.Count == 0)
        {
            throw new AuthorRunException($"The sources file '{path}' names no subject.");
        }

        foreach (var (subject, source) in sources)
        {
            if (string.IsNullOrWhiteSpace(source?.System) || string.IsNullOrWhiteSpace(source.Table) || string.IsNullOrWhiteSpace(source.KeyField))
            {
                throw new AuthorRunException($"The sources file '{path}': '{subject}' needs a system, a table and a keyField.");
            }
        }

        return sources;
    }

    internal static async Task<IReadOnlyList<Declaration>> ReadCatalogAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var stream = File.OpenRead(path);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync(stream, OntologyJson.Default.IReadOnlyListDeclaration, cancellationToken).ConfigureAwait(false)
                    ?? throw new AuthorRunException($"The catalog '{path}' holds null, not an array of declarations.");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            throw new AuthorRunException($"The catalog '{path}' cannot be read: {e.Message}", e);
        }
    }
}

/// <summary>The run cannot go on with what it was given; the message says what is wrong.</summary>
internal sealed class AuthorRunException : Exception
{
    public AuthorRunException(string message)
        : base(message)
    {
    }

    public AuthorRunException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
