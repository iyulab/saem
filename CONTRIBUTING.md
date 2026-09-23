# Contributing to saem

Thanks for your interest. Please read this before opening an issue or a pull request.

## Status

saem is pre-implementation — the architecture is settled and described in the README, but the
code is not written yet. External code contributions are not being solicited at this stage.
Issues that report gaps or inconsistencies in the design as described are welcome; feature PRs
against a codebase that does not exist yet are not.

## Contributor License Agreement (CLA)

saem is dual-licensed: AGPL-3.0 for open-source use, with a separate commercial license
available. Offering a commercial license depends on the project holding clear, unencumbered
rights to every contribution — a single external contribution without an assigned or licensed
copyright can block that file from being included in the commercial offering.

**Every external code contribution requires signing a CLA before it can be merged.** The CLA
does not transfer ownership of your contribution; it grants the project the rights needed to
distribute your contribution under both the open-source and commercial license terms.

Signing is a file in your own pull request: read [CLA.md](CLA.md) and add
`.github/cla/v1/<your-github-login>.md` (login in lowercase) with the content given there under
"Signing". A CI check on every pull request looks for that file and says what is missing if it is
not there. There is no bot and no external service. Members of the iyulab organization are not
asked to sign, since their contributions are already made under iyulab's copyright.

Note: this requirement does not extend to non-code content that saem does not itself own the
copyright to (for example, standards-derived reference data folded into a domain pack). That kind
of contribution needs a different kind of provenance check, not a CLA, and is out of scope for
this repository at its current stage.

## No external AGPL/GPL dependencies

saem does not take on external dependencies licensed under AGPL, GPL, or LGPL. This keeps the
project able to offer a commercial license without a copyleft dependency forcing that license
to become AGPL by inheritance. If a change would introduce one, expect it to be declined
regardless of technical merit — open an issue first if you think an exception is warranted.

## Reporting issues

Bug reports and design questions are welcome via GitHub Issues. For anything involving a
potential security vulnerability, see [SECURITY.md](SECURITY.md) instead of opening a public
issue.

## Pull requests

Given the pre-implementation status above, please open an issue to discuss scope before
investing time in a PR. When code contributions do open up, the PR template will walk through
the CLA step.
