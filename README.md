# saem

**Saem** — wellspring of knowledge, where scattered streams seep in and rise again

> An on-premises intelligence layer that sits on top of an organization's existing
> line-of-business systems, connects their scattered data into a single ontology, and lets you
> build and deploy grounded answers and widgets on top of it.

**Status: pre-implementation.** The architecture is settled; the code is not written yet.
This README describes what saem is meant to be, not what it currently does.

---

## The problem

Business knowledge lives across several line-of-business systems (ERP, MES, CMMS, and others),
and each system is complete only within itself. Questions answerable inside one system are
already solved. The ones that are not are the questions that **cross system boundaries**.

How that gap is handled today, and where each approach breaks:

| Current approach | Where it breaks |
|---|---|
| People walk between systems and assemble the answer by hand | Cost scales with human time, and the result is never reused |
| BI and reporting dashboards | Only show the axes someone chose in advance; a new question means a new development request |
| Vector RAG chatbots | Cannot answer relational or structural questions (multi-hop, cross-system), and **cannot show the path that produced the answer** |

The third one is why saem exists. The only structural advantage an ontology layer has over
retrieval is that it can **present the path it walked**.

## Principles

**Explainability is non-negotiable, and it is enforced by the contract — not by a prompt.**
A response is a structure of `{claim, sources[], path[]}`, and **a claim with no sources cannot be
expressed at all**. Natural language is a rendering of that structure. When a query returns
nothing, saem refuses without invoking the model. If a principle is non-negotiable, the format
must not be able to express a violation. This costs real coverage, real latency, and — deliberately —
**synthesis**: "defects on this line are trending up" is a claim that lives in no single record.

**saem never writes to source systems.** It is read-only. When something must be recorded, it goes
into saem's own space, and that space is bounded by a **reducibility rule**: every ontology
instance node must reduce to at least one source record. Nodes that unify entities across systems
are references to several records, and concepts that exist only in the organization (a line, an
equipment group, a crew) may exist only as **groupings** — they carry no facts of their own, and
their grounding is their members. This is the data-model form of the same principle: no
ungrounded answers, no ungrounded nodes.

**saem does not reimplement authorization — but it enforces it at two points.**
What is materialized never passes through query-time federation, so delegating to the source
system at query time alone would leave the relationship graph outside the permission model.

| Point | Covers | How |
|---|---|---|
| Materialization | Graph, indexes, embeddings | Materialized per group profile; instance edges inherit source-record visibility |
| Query | Record bodies | Federated with the asking user's own credentials; row- and field-level rules are enforced by the source system |

**Edges by group, bodies by user.** A documented residual: within a group, differences between
individuals are not reflected in edge visibility — group partitioning narrows that exposure but
does not eliminate it.

**What grows is models, not code.** Connectivity is limited to a small set of generic primitives.
Growth happens through ontology assets — domain packs and mapping templates.

## How it works

**Hybrid data ownership, in three layers.** saem materializes ontology structure and
relationships; record bodies are federated at query time and are never materialized. Ownership of
the original data stays with the source system. What is materialized is partitioned by visibility:

| Layer | Visibility |
|---|---|
| Type and relationship **schema** | Organization-wide |
| **Instance edges** | Inherit the source record's visibility |
| Indexes, graphs, **embeddings** | Built only within what the channel's scope permits |

Embeddings are the sharpest edge: they are derived from record content, so content crosses the
boundary at materialization time — before anyone asks a question.

**Build pipeline.**

```
  saem-agent                channel                   surface
  ──────────                ───────                   ───────
  knowledge scoping    →    access assignment    →    chat widget
  (optional)                users / groups            saem-artifact widget
  API-only                  (also the materialization (both call the agent API)
                             partition key)
```

The contract is the first-class output. A **`saem-agent` is a contract, not a runtime** — saem does
not build an agent loop. saem owns an MCP **server**; the built-in chat surface consumes it through
an MCP **client**. That makes "the built-in UI is a reference consumer" structural rather than
aspirational: the built-in chat connects over the same contract everyone else does.

**Ontology authoring** combines standard domain packs, LLM-assisted extraction and mapping, and
human review. Changes flow as changesets through simulate → apply → rollback.

**Model access** goes through a single provider-neutral entry point, so saem runs fully
on-premises against whatever inference an organization already operates — a local model farm or a
hosted provider. Local inference is available out of the box, not required.

## Consumption modes

saem is self-contained and can also be integrated as middleware. All three modes are first-class:

1. **Standalone** — an organization runs saem and manages and queries the ontology inside it
2. **Client-side widget embedding** — a host application embeds saem's chat or `saem-artifact` widgets
3. **API / MCP only** — a host application brings its own UI and uses saem as a knowledge provider

## What saem is not

- **Not an app builder.** saem does not author domain entities of its own
- **Not a write path.** It does not write back to source systems; acting is the consuming system's job
- **Not a BI or reporting dashboard.** It shows relationships, not figures
- **Not an agent runtime.** The loop, session handling, and compaction belong to the agent host
- **Not a workflow execution engine**
- **Not an ETL platform, and not an LLM training tool**

## Terminology

**`saem-agent`** — a contract: a knowledge scope, the set of MCP tools it exposes, and the response
contract. API-only.

**`saem-artifact`** — a deployable unit produced by the builder: an ontology contract binding,
zero or more generated UI units, and surface configuration.

> **Two name collisions, one strategy.** `vivarium` uses `artifact` for a single generated UI unit,
> and both `vivarium` and `ironhive` use `agent` for things a `saem-agent` is not — a
> changeset-authoring harness and an execution engine, respectively. A `saem-artifact` *contains*
> vivarium artifacts; a `saem-agent` is a different category altogether (data, not a runtime).
> The prefix keeps them apart in both cases.

**Chat** is a built-in capability, not a `saem-artifact`. What gets built is a chat instance with a
particular knowledge scope.

## Built on

- [Formbase](https://github.com/iyulab/Formbase) — integrated at the container boundary, not as an
  SDK reference; it stores what saem materializes
- [vivarium](https://github.com/iyulab/vivarium) family — sandboxed UI runtime and the changeset lifecycle
- [iron-prow](https://github.com/iyulab/iron-prow) — provider-neutral inference gateway
- [lm-supply](https://github.com/iyulab/lm-supply) — local inference, available by default

Two further pieces are consumed as libraries rather than built here: **agent hosting**
(the loop, session handling, and the MCP client that the built-in chat surface runs on) and
**hybrid retrieval** (vector plus keyword search over what saem materializes).
The specific packages are not pinned in this README yet.

## License

AGPL-3.0 with a commercial license available. External contributions require a CLA.
saem does not take external AGPL/GPL dependencies.
