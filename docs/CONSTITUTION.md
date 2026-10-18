# saem Constitution

> The boundaries every maintainer of this project shares: what saem is, what it is not, and what it
> does not yet claim. These are not task instructions — they are the reference point to return to
> when a judgment could go either way. The [README](../README.md) explains the same boundaries in
> prose; this document states them as clauses, so a proposal can be checked against a numbered rule.

---

## 1. Explainability is non-negotiable

Every claim saem returns reduces to the source records it was read from and the path that reached
them. A response is a structure of claim, sources and path, and **a claim with no source cannot be
expressed** — the format cannot carry a violation. When a lookup returns nothing, saem refuses; it
does not invoke a model to fill the gap.

This has a price, and the clause includes it: coverage, latency, and synthesis. A conclusion drawn
across several facts lives in no single record, so it is the consumer's to draw, not saem's to vouch
for.

## 2. Authorization is not reimplemented

Record bodies are read with the credentials of the person asking, or of the person an AI is asking
for. Row- and field-level rules are the source system's, enforced by the source system — saem keeps
no copy of them to evaluate elsewhere.

Read this clause together with clause 4: it states the design, and clause 4 states how far that
design has been shown.

## 3. Read-only toward source systems

saem never writes to a system it reads. What must be recorded goes into saem's own space, and that
space is bounded by the reducibility rule: every ontology instance node reduces to at least one
source record, and a concept that exists only in the organization exists only as a grouping of such
records, carrying no facts of its own.

## 4. What saem does not yet claim

Permission enforcement — at query time and at materialization — is a design, not a demonstrated
property, until it is shown against real source systems. Until then every statement about it is
written as a goal, not as a result. Showing the mechanism over synthetic sources is a step toward
that, and is described as exactly that step, not as the property itself.

## 5. The surfaces are saem's

The answer contract, the built-in chat surface, and the widgets built on it are part of saem. They
consume the same contract any external consumer does; none of them reads around it.

## 6. Not an agent runtime

saem serves facts over a contract. The loop, sessions, compaction, and synthesis across facts belong
to the consumer.

## 7. Permanent non-goals

saem is not, and will not become:

- an app builder — it authors no domain entities of its own;
- a write path back to source systems — acting is the consuming system's job;
- a BI or reporting dashboard — it shows relationships, not figures;
- a workflow execution engine;
- an ETL platform, or an LLM training tool.

## 8. Order, not dates

The roadmap states sequence only. A date in a public document is a re-check deadline for a claim
about the world, never a delivery promise.

## 9. Amendment

1. A change to these clauses is proposed with evidence — from the code, or from what was observed.
2. Which side is right is settled first: if a clause is wrong, the clause is amended; if the project
   has drifted from a clause, the project is fixed.
3. An amendment is confirmed by the maintainer.
4. An amendment is complete when every document stating the same fact says it the same way, and the
   amended clause carries its date.
