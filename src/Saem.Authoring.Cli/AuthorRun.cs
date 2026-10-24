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
    /// <exception cref="AuthorRunException">An input cannot be read, or an output exists and the run was not told to replace it.</exception>
    public static async Task RunAsync(
        AuthorOptions options,
        IOntologyProposer proposer,
        IRecordSample records,
        IStructureSource structures,
        IReadOnlyCollection<ModelCallRetry> retries,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(retries);
        ArgumentNullException.ThrowIfNull(log);

        foreach (var output in new[] { options.ReviewPath, options.DraftPath, options.ProposalsPath })
        {
            if (!options.Overwrite && File.Exists(output))
            {
                throw new AuthorRunException($"'{output}' exists; pass --overwrite to replace it.");
            }
        }

        var pass = ReviewSettlement.PassId(DateTimeOffset.UtcNow);
        var sources = await ReadSourcesAsync(options.SourcesPath, cancellationToken).ConfigureAwait(false);
        var existing = await ReadCatalogAsync(options.CatalogPath, cancellationToken).ConfigureAwait(false);
        await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Sampling {sources.Count} subjects ({options.RecordsPerSubject} documents each) against {existing.Count} declarations in force; asking {options.Model}.")).ConfigureAwait(false);

        var drafted = await new DeclarationAuthoring(proposer, records, structures)
            .DraftAsync(sources, existing, options.RecordsPerSubject, cancellationToken).ConfigureAwait(false);

        await File.WriteAllTextAsync(options.ReviewPath, drafted.ToReview() + RetriesSection(retries), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(options.DraftPath, drafted.Draft.ToJson(), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(options.ProposalsPath, new ProposedDeclarations(pass, drafted.Draft.Added).ToJson(), cancellationToken).ConfigureAwait(false);
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
