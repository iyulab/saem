using System.Globalization;

namespace Saem.Authoring.Cli;

/// <summary>
/// What one run of <c>saem-author</c> is told: where the documents are, what the ontology already declares,
/// which model to ask, and where the review and the draft go. Secrets are read from the environment, never
/// from the command line, where a shell history or a process listing would keep them.
/// </summary>
internal sealed record AuthorOptions(
    Uri Formbase,
    string? FormbaseNamespace,
    string? FormbaseToken,
    string SourcesPath,
    string CatalogPath,
    Uri ModelEndpoint,
    string Model,
    string? ModelApiKey,
    bool ModelStream,
    TimeSpan ModelTimeout,
    int Retries,
    int RecordsPerSubject,
    string ReviewPath,
    string DraftPath,
    string ProposalsPath,
    bool Overwrite)
{
    public const string FormbaseTokenVariable = "SAEM_FORMBASE_TOKEN";
    public const string ModelApiKeyVariable = "SAEM_MODEL_API_KEY";

    public const string Usage = """
        saem-author — draft ontology declarations from documents in Formbase, for a person to review.

        Usage:
          saem-author --formbase <url> --sources <file> --catalog <file>
                      --model-endpoint <url> --model <name> [options]

        Required:
          --formbase <url>               The Formbase instance the documents are read from.
          --sources <file>               JSON object: subject (form type) -> { "system", "table", "keyField",
                                         "columns"?, "record"? } — where its rows stand for a lookup.
          --catalog <file>               The ontology declarations in force. A missing file is an empty ontology.
          --model-endpoint <url>         An OpenAI-compatible base address, version segment included (…/v1/).
          --model <name>                 The model to ask.

        Options:
          --formbase-namespace <name>    The Formbase namespace to read; omitted, the instance's own.
          --model-stream                 Ask for the answer as it is written (behind a gateway that cuts a slow first byte).
          --model-timeout-minutes <n>    How long to wait on the model before a call fails (default 10).
          --retries <n>                  How many more times a call that fails transiently is tried (default 1).
          --records <n>                  How many documents to sample from each subject (default 20).
          --review <path>                Where the review document goes (default review.md).
          --draft <path>                 Where the draft catalog goes (default draft.json). Pass the catalog
                                         itself, with --overwrite, to review the draft as a diff of that file.
          --proposals <path>             Where the pass's proposals are kept until the review is settled
                                         (default proposals.json) — read by saem-author settle.
          --overwrite                    Replace an output file if it exists.

        Environment:
          SAEM_FORMBASE_TOKEN            A bearer token sent to Formbase, when something in front of it asks for one.
          SAEM_MODEL_API_KEY             A bearer token sent to the model endpoint.

        Nothing is written but the three files: the catalog changes when a person accepts the draft as an edit
        of it. The draft is not simulated or applied against the sources.

        Once the review is in the catalog, record what was taken:
          saem-author settle --proposals <file> --catalog <file> --records <file> [--settled-by <id>]
        """;

    /// <summary>Reads the command line, and the secrets from <paramref name="environment"/>.</summary>
    /// <exception cref="AuthorUsageException">The command line is incomplete or malformed.</exception>
    public static AuthorOptions Parse(IReadOnlyList<string> args, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "--model-stream" or "--overwrite")
            {
                flags.Add(arg);
            }
            else if (arg is "--formbase" or "--formbase-namespace" or "--sources" or "--catalog" or "--model-endpoint" or "--model"
                     or "--model-timeout-minutes" or "--retries" or "--records" or "--review" or "--draft" or "--proposals")
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new AuthorUsageException($"{arg} needs a value.");
                }

                if (!values.TryAdd(arg, args[++i]))
                {
                    throw new AuthorUsageException($"{arg} is given more than once.");
                }
            }
            else
            {
                throw new AuthorUsageException($"Unknown argument '{arg}'.");
            }
        }

        return new AuthorOptions(
            Formbase: AbsoluteUri(Required("--formbase"), "--formbase"),
            FormbaseNamespace: values.GetValueOrDefault("--formbase-namespace"),
            FormbaseToken: Secret(FormbaseTokenVariable),
            SourcesPath: Required("--sources"),
            CatalogPath: Required("--catalog"),
            ModelEndpoint: AbsoluteUri(Required("--model-endpoint"), "--model-endpoint"),
            Model: Required("--model"),
            ModelApiKey: Secret(ModelApiKeyVariable),
            ModelStream: flags.Contains("--model-stream"),
            ModelTimeout: TimeSpan.FromMinutes(Count("--model-timeout-minutes", 10, minimum: 1)),
            Retries: Count("--retries", 1, minimum: 0),
            RecordsPerSubject: Count("--records", 20, minimum: 1),
            ReviewPath: values.GetValueOrDefault("--review", "review.md"),
            DraftPath: values.GetValueOrDefault("--draft", "draft.json"),
            ProposalsPath: values.GetValueOrDefault("--proposals", "proposals.json"),
            Overwrite: flags.Contains("--overwrite"));

        string Required(string name) =>
            values.TryGetValue(name, out var value) ? value : throw new AuthorUsageException($"{name} is required.");

        int Count(string name, int fallback, int minimum)
        {
            if (!values.TryGetValue(name, out var text))
            {
                return fallback;
            }

            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= minimum
                ? value
                : throw new AuthorUsageException($"{name} takes a whole number of at least {minimum}, not '{text}'.");
        }

        string? Secret(string variable) => environment(variable) is { Length: > 0 } value ? value : null;
    }

    // A base address must end with a slash, or the last segment is replaced when a relative path is resolved against it.
    private static Uri AbsoluteUri(string text, string name) =>
        Uri.TryCreate(text.EndsWith('/') ? text : text + "/", UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri
            : throw new AuthorUsageException($"{name} takes an http(s) address, not '{text}'.");
}

/// <summary>The command line cannot be run as given; the message says what is wrong.</summary>
internal sealed class AuthorUsageException(string message) : Exception(message);
