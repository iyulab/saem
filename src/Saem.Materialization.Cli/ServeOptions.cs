namespace Saem.Materialization.Cli;

/// <summary>
/// What <c>saem-materialize serve</c> is told: the same group a one-shot run is told, and the address to listen on. The
/// secrets that let a caller ask for work come from the environment, like every secret here.
/// </summary>
internal sealed record ServeOptions(MaterializeOptions Run, Uri Listen, IReadOnlyList<string> Secrets)
{
    /// <summary>The secret a caller presents as a bearer token.</summary>
    public const string SecretVariable = "SAEM_EXECUTOR_SECRET";

    /// <summary>A second secret accepted alongside the first, so a secret is replaced without a moment when callers are refused.</summary>
    public const string NextSecretVariable = "SAEM_EXECUTOR_SECRET_NEXT";

    /// <summary>The shortest secret accepted: a guessable one protects nothing.</summary>
    public const int MinimumSecretLength = 24;

    public const string Usage = """
        saem-materialize serve — keep one group's executor running, to be asked for work by the administration console.

        Usage:
          saem-materialize serve --catalog <file> --sources <file> --group <name> --base-iri <iri> --store <url> --listen <url> [options]

        The arguments are those of a pass (saem-materialize --help), but --changed, which a request names instead; and
          --listen <url>                 The address to serve on, e.g. https://0.0.0.0:8443 — behind a proxy that ends TLS,
                                         http://127.0.0.1:8080. The secret is sent in every request: do not serve it over
                                         plain HTTP across a network.

        Environment, besides a pass's:
          SAEM_EXECUTOR_SECRET           What a caller presents as "Authorization: Bearer <secret>" (at least 24 characters).
          SAEM_EXECUTOR_SECRET_NEXT      A second secret accepted too — set it, move the callers over, then make it the
                                         first and drop it.

        One executor serves one group: it holds that group's secrets and no other's. It runs one job at a time.
          POST   /jobs                   {"kind": "<kind>", "requestedBy": "<who asked>"} -> 202, the job; 409 while
                                         another job runs. The kinds are the commands of a run:
                                           verify        what a pass would give now against the store, writing nothing
                                           check         the declarations against the sources (the report comes back too)
                                           pass          the edges, then what the identities find, written to the store
                                           changed-pass  the same, reading only the rows changed since the last pass
                                           fit           an identity's linkage rule fitted, the fit kept
                                           judge         a person's judgment of a pair, filed as theirs (requestedBy is who judged)
                                         "arguments" carries what a kind takes: {"identity": "<name>"} for fit, and for judge
                                         {"identity": "<name>", "first": "<iri>", "second": "<iri>", "same": true | false}.
          GET    /jobs                   The most recent jobs.
          GET    /jobs/{id}              One job: its state, exit code and the lines the command prints.
          POST   /jobs/{id}/cancel       Stops a running job.
        Jobs are kept in memory only; the group's store and the reports are what remains.
        """;

    /// <exception cref="MaterializeUsageException">The command line is incomplete or malformed, or no usable secret is set.</exception>
    public static ServeOptions Parse(IReadOnlyList<string> args, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);

        var rest = new List<string>();
        string? listen = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--changed")
            {
                throw new MaterializeUsageException("serve takes no --changed; a request names the kind of pass it wants.");
            }

            if (args[i] != "--listen")
            {
                rest.Add(args[i]);
                continue;
            }

            if (listen is not null)
            {
                throw new MaterializeUsageException("--listen is given more than once.");
            }

            listen = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : throw new MaterializeUsageException("--listen needs a value.");
        }

        var run = MaterializeOptions.Parse(rest, environment);
        if (listen is null)
        {
            throw new MaterializeUsageException("--listen is required.");
        }

        if (!Uri.TryCreate(listen, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
        {
            throw new MaterializeUsageException($"--listen takes an http(s) address, not '{listen}'.");
        }

        var secrets = new List<string>();
        foreach (var variable in new[] { SecretVariable, NextSecretVariable })
        {
            if (environment(variable) is not { Length: > 0 } secret)
            {
                if (variable == SecretVariable)
                {
                    throw new MaterializeUsageException($"{SecretVariable} is required: it is what callers present to ask for work.");
                }

                continue;
            }

            if (secret.Length < MinimumSecretLength)
            {
                throw new MaterializeUsageException($"{variable} must be at least {MinimumSecretLength} characters.");
            }

            if (secrets.Contains(secret, StringComparer.Ordinal))
            {
                throw new MaterializeUsageException($"{NextSecretVariable} must differ from {SecretVariable}.");
            }

            secrets.Add(secret);
        }

        return new ServeOptions(run, address, secrets);
    }
}
