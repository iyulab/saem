namespace Saem.Materialization.Cli;

/// <summary>
/// What one run of <c>saem-materialize</c> is told: the ontology, the group, where its accounts read and where
/// its edges go. Secrets are read from the environment, never from the command line, where a shell history or
/// a process listing would keep them.
/// </summary>
internal sealed record MaterializeOptions(
    string CatalogPath,
    string SourcesPath,
    string Group,
    Uri BaseIri,
    Uri Store,
    string? StoreNamespace,
    string? StoreToken,
    bool ChangedOnly = false)
{
    public const string StoreTokenVariable = "SAEM_STORE_TOKEN";

    /// <summary>The prefix of the variable that holds a source's secret: the system's name follows, upper-cased, other characters as <c>_</c>.</summary>
    public const string SourceSecretPrefix = "SAEM_SOURCE_SECRET_";

    public const string Usage = """
        saem-materialize — build one group's instance edges, and what its identities find, from what its accounts may
        read, into its own store.

        Usage:
          saem-materialize --catalog <file> --sources <file> --group <name> --base-iri <iri> --store <url> [options]

        Required:
          --catalog <file>               The ontology declarations in force.
          --sources <file>               JSON object: system -> how this group's account reads it —
                                         { "postgres": "<connection>", "user": "<login>" },
                                         { "mysql": "<connection>", "user": "<login>" } or
                                         { "morphdb": "<url>", "projectId": "<id>" }. Connection strings name no login.
          --group <name>                 The group, as people's tokens name it in their groups claim.
          --base-iri <iri>               The absolute IRI entities are named under — the server's Saem:Materialization:BaseIri.
          --store <url>                  The group's Formbase: started over the group's own project and confined secret.

        Options:
          --store-namespace <name>       The Formbase namespace to write; omitted, the instance's own.
          --changed                      Read only the rows changed since the last pass, where a relation's leaving type
                                         declares changedAt and the last pass filed where it read to; every other relation
                                         is read whole. Identities are not matched — a thing needs every member read.

        Environment:
          SAEM_SOURCE_SECRET_<SYSTEM>    The group account's password (postgres, mysql) or bearer secret (morphdb) for
                                         each system in the sources file — e.g. SAEM_SOURCE_SECRET_MES for "mes".
          SAEM_STORE_TOKEN               One of the store Formbase's access keys (Formbase:Access:Keys), sent as a bearer token.

        Only the relations' leaving types are read, and only their relation fields and record columns. The store
        is made to hold exactly this pass's edges: unchanged ones are not sent, changed ones are corrected, and
        ones no longer found are retired. Then each identity declaring a field match reads its two types' matched
        fields, and each identity by a linkage rule reads its compared fields and scores every pair with the fit the
        store keeps (saem-materialize fit --help) — one with no fit, or a fit of a rule since changed, is left out and
        the pass says so. The store is made to hold what they find: canonical ids carried on from the last pass (a merge
        keeps the older, the part split off gets a new one), each entity's cross-reference and the grounds of each pair
        — the rule and its score. An identity a person settles (decision "confirmed") files its pairs as grounds only:
        for a linkage rule, every pair scored one thing or undecided, each a candidate for that person.

        Every pass files, per relation whose type declares changedAt, the latest change it read from. A --changed pass
        reads on from there: each changed row corrects or retires its own edge, and the rest stand. It cannot see a
        row deleted, one the group can no longer read, or one a long transaction wrote with an earlier change time —
        a whole pass does: run --changed often and whole passes now and then.

        To compare the store with what a pass would give now, writing nothing: saem-materialize verify <the same
        arguments> — exit status 1 when it disagrees (edges or identified things missing, different or no longer
        given, or documents a projection skipped); the pass rebuilds it.
        To check the declarations against the sources first, as one account: saem-materialize check --help.
        To fit an identity's linkage rule to the group's records: saem-materialize fit --help.
        To file a person's judgment of a pair an identity is about: saem-materialize judge --help.
        """;

    /// <summary>The variable holding the secret of <paramref name="system"/>.</summary>
    public static string SecretVariable(string system) =>
        SourceSecretPrefix + string.Concat(system.ToUpperInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_'));

    /// <summary>Reads the command line, and the store's token from <paramref name="environment"/>.</summary>
    /// <exception cref="MaterializeUsageException">The command line is incomplete or malformed.</exception>
    public static MaterializeOptions Parse(IReadOnlyList<string> args, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--changed")
            {
                if (!values.TryAdd(arg, ""))
                {
                    throw new MaterializeUsageException($"{arg} is given more than once.");
                }

                continue;
            }

            if (arg is not ("--catalog" or "--sources" or "--group" or "--base-iri" or "--store" or "--store-namespace"))
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

        var baseIri = Required("--base-iri");
        return new MaterializeOptions(
            CatalogPath: Required("--catalog"),
            SourcesPath: Required("--sources"),
            Group: Required("--group"),
            BaseIri: Uri.TryCreate(baseIri, UriKind.Absolute, out var iri) ? iri : throw new MaterializeUsageException($"--base-iri takes an absolute IRI, not '{baseIri}'."),
            Store: HttpAddress(Required("--store"), "--store"),
            StoreNamespace: values.GetValueOrDefault("--store-namespace"),
            StoreToken: environment(StoreTokenVariable) is { Length: > 0 } token ? token : null,
            ChangedOnly: values.ContainsKey("--changed"));

        string Required(string name) =>
            values.TryGetValue(name, out var value) ? value : throw new MaterializeUsageException($"{name} is required.");
    }

    // A base address must end with a slash, or the last segment is replaced when a relative path is resolved against it.
    private static Uri HttpAddress(string text, string name) =>
        Uri.TryCreate(text.EndsWith('/') ? text : text + "/", UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri
            : throw new MaterializeUsageException($"{name} takes an http(s) address, not '{text}'.");
}

/// <summary>The command line or the sources file cannot be run as given; the message says what is wrong.</summary>
internal sealed class MaterializeUsageException(string message) : Exception(message);
