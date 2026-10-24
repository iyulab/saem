namespace Saem.Materialization.Cli;

/// <summary>
/// <c>saem-materialize judge</c>: a person's judgment of one pair an identity is about, filed in the group's store as a
/// ground of its own. The next pass takes it over the rule's (saem D-81 ②).
/// </summary>
internal static class JudgeRun
{
    public const string Usage = """
        saem-materialize judge — file a person's judgment of a pair in a group's store.

        Usage:
          saem-materialize judge --catalog <file> --store <url> --identity <name> --first <iri> --second <iri>
                                 (--same | --different) --by <id> [--store-namespace <name>]

          --catalog <file>               The ontology declarations in force.
          --store <url>                  The group's Formbase.
          --identity <name>              The declared identity the pair is about.
          --first <iri>, --second <iri>  The two entities, by the IRIs the store names them by.
          --same | --different           Whether they are one thing or two.
          --by <id>                      An opaque id of the person judging — never a name.

        Environment:
          SAEM_STORE_TOKEN               One of the store Formbase's access keys, sent as a bearer token.

        A judgment wins over the rule run again: a pair judged the same is one thing whatever the rule says; a pair
        judged different leaves the rule's thing, both of them. For an identity a person settles ("confirmed"), the
        rule's pairs are candidates and only what people judge the same becomes a thing. A later judgment of the same
        pair corrects it. The store's things change on the next pass.
        """;

    /// <exception cref="MaterializeUsageException">The command line is incomplete or malformed.</exception>
    public static JudgeOptions Parse(IReadOnlyList<string> args, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        bool? same = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "--same" or "--different")
            {
                if (same is not null)
                {
                    throw new MaterializeUsageException("--same and --different are given more than once together.");
                }

                same = arg == "--same";
                continue;
            }

            if (arg is not ("--catalog" or "--store" or "--store-namespace" or "--identity" or "--first" or "--second" or "--by"))
            {
                throw new MaterializeUsageException($"Unknown argument '{arg}'.");
            }

            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new MaterializeUsageException($"{arg} needs a value.");
            }

            if (!values.TryAdd(arg, args[++i]))
            {
                throw new MaterializeUsageException($"{arg} is given more than once.");
            }
        }

        var store = Required("--store");
        return new JudgeOptions(
            Required("--catalog"),
            Uri.TryCreate(store.EndsWith('/') ? store : store + "/", UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                ? uri
                : throw new MaterializeUsageException($"--store takes an http(s) address, not '{store}'."),
            values.GetValueOrDefault("--store-namespace"),
            environment(MaterializeOptions.StoreTokenVariable) is { Length: > 0 } token ? token : null,
            Required("--identity"),
            Required("--first"),
            Required("--second"),
            same ?? throw new MaterializeUsageException("--same or --different is required."),
            Required("--by"));

        string Required(string name) =>
            values.TryGetValue(name, out var value) ? value : throw new MaterializeUsageException($"{name} is required.");
    }

    /// <exception cref="IdentityStoreException">The store refused the judgment.</exception>
    public static async Task RunAsync(
        Ontology.Ontology ontology, JudgeOptions options, FormbaseIdentityStore store, DateTimeOffset at, TextWriter output, CancellationToken cancellationToken)
    {
        await store.JudgeAsync(ontology, new PersonJudgment(options.Identity, options.First, options.Second, options.Same, options.By, at), cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(
            $"{options.Identity}: {options.First} and {options.Second} judged {(options.Same ? "one thing" : "two things")} by {options.By}; the next pass takes it in.").ConfigureAwait(false);
    }
}

/// <summary>What one <c>saem-materialize judge</c> is told.</summary>
internal sealed record JudgeOptions(
    string CatalogPath, Uri Store, string? StoreNamespace, string? StoreToken, string Identity, string First, string Second, bool Same, string By);
