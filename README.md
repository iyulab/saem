# saem

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

**Explainability is non-negotiable.** Every answer must reduce to sources and a path through the
ontology. If saem cannot show the grounding, it does not answer. This costs real coverage and
real latency — that is the price, deliberately paid.

**saem never writes to source systems.** It is read-only. When something must be recorded, it goes
into saem's own space, and that space holds only derived, annotation, and status data attached to
source records — never independently existing domain entities.

**saem does not reimplement authorization.** Access is controlled at the channel level; row- and
field-level permissions are enforced by the source systems themselves, because federated reads are
performed with the asking user's own credentials.

**What grows is models, not code.** Connectivity is limited to a small set of generic primitives.
Growth happens through ontology assets — domain packs and mapping templates.

## How it works

**Hybrid data ownership.** saem materializes ontology structure, metadata, relationships, indexes,
graphs, and embeddings. Record bodies are federated at query time. Ownership of the original data
stays with the source system.

**Build pipeline.**

```
  agent                     channel                   surface
  ─────                     ───────                   ───────
  knowledge scoping    →    access assignment    →    chat widget
  (optional)                users / groups            saem-artifact widget
  API-only                                            (both call the agent API)
```

The contract is the first-class output. An **agent** is API-only; every surface reaches the
ontology through it. The built-in chat UI is a reference consumer of that same contract.

**Ontology authoring** combines standard domain packs, LLM-assisted extraction and mapping, and
human review. Changes flow as changesets through simulate → apply → rollback.

## Consumption modes

saem is self-contained and can also be integrated as middleware. All three modes are first-class:

1. **Standalone** — an organization runs saem and manages and queries the ontology inside it
2. **Client-side widget embedding** — a host application embeds saem's chat or `saem-artifact` widgets
3. **API / MCP only** — a host application brings its own UI and uses saem as a knowledge provider

## What saem is not

- **Not an app builder.** saem does not author domain entities of its own
- **Not a write path.** It does not write back to source systems; acting is the consuming system's job
- **Not a BI or reporting dashboard.** It shows relationships, not figures
- **Not a workflow execution engine**
- **Not an ETL platform, and not an LLM training tool**

## Terminology

**`saem-artifact`** — a deployable unit produced by the builder: an ontology contract binding,
zero or more generated UI units, and surface configuration.

> Note: `vivarium` uses `artifact` for a single generated UI unit. A `saem-artifact` *contains*
> those. The prefix exists to keep the two apart.

**Chat** is a built-in capability, not a `saem-artifact`. What gets built is a chat instance with a
particular knowledge scope.

## Built on

- [Formbase](https://github.com/iyulab/Formbase) — integrated at the container boundary, not as an SDK reference
- [vivarium](https://github.com/iyulab/vivarium) family — sandboxed UI runtime and the changeset lifecycle

## License

AGPL-3.0 with a commercial license available. External contributions require a CLA.
saem does not take external AGPL/GPL dependencies.
