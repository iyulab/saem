# Security Policy

## Reporting a vulnerability

Please **do not** open a public GitHub issue for a suspected security vulnerability.

Use GitHub's private vulnerability reporting for this repository (the "Report a vulnerability"
button under the repository's Security tab). That opens a private advisory visible only to the
maintainers until a fix is ready, which avoids disclosing an exploitable issue before a patch
exists.

If private reporting is not available to you for some reason, open an issue asking to be
pointed to an alternate private channel — without describing the vulnerability itself in the
public issue.

## Scope

The parts that run are in scope: the MCP server (`/mcp`), the administration console, the materialization executor
(`saem-materialize serve`) and the `saem-author` tool. Reports about anything that lets a caller read what its token does not
allow, get past authentication, run a job without an executor secret, or send data somewhere it was not configured to go are
the ones this policy exists for. How each part decides who may do what is in the [README](README.md) (the MCP server and
administration console sections, and the executor under materialization).

## Supported versions

No versioned release exists yet. Once releases begin, supported versions will be listed here.
