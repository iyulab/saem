# saem

**Saem** — Korean for a natural spring: scattered streams seep in, gather, and rise again
as a wellspring of knowledge

> An on-premises context layer for AI that acts on someone's behalf. It sits read-only on top of
> an organization's existing line-of-business systems, connects their scattered data into a single
> ontology, and serves grounded facts from it — to agents and generated applications, and to the
> answers and widgets built and deployed on saem itself. It is designed so that every query runs as
> the person being acted for, and every fact it returns carries its source.

**Status: early implementation — nothing is published.** What runs: connectors that read
PostgreSQL, MySQL, MorphDB and Formbase sources; an authoring pass (`saem-author`) that drafts the
ontology's declarations from Eyu's proposals for a person to review; a lookup walk that reads each hop
through a session opened as the person it is for; an MCP server whose one lookup tool runs as the
person a request's bearer token names; and a first materialization pass (`saem-materialize`) that
keeps each group's relation edges in that group's own store (all below). A MorphDB source is read with
the person's own token, exchanged for one meant for it; the relational sources still log in with a
password per person, until their drivers can present a token. All of it runs on synthetic fixtures,
not yet on a real organization's systems — so much of this README, the permission principle
included, still describes what saem is meant to be rather than what it has shown. The architecture
and ADRs are still under active revalidation. What comes next is under [Roadmap](#roadmap).

---

## The problem

Business knowledge lives across several line-of-business systems (ERP, MES, CMMS, and others),
and each system is complete only within itself. Questions answerable inside one system are
already solved. The ones that are not are the questions that **cross system boundaries**.

**The same gap, with a different reader.** More and more of what reads these systems is not a
person but an AI acting for one — an agent, or an application someone generated for their own
work. Such a reader needs what a person needs, and one thing more: it must see **only what the
person it acts for may see**. A copy of the source's access rules evaluated in some other store,
or one service account shared by everyone, stops being acceptable once the reader is a delegate.
(saem's own answer to this has a documented residual at the edge level — see *Principles*.)

How that gap is handled today, and where each approach breaks:

| Current approach | Where it breaks |
|---|---|
| People walk between systems and assemble the answer by hand | Cost scales with human time, and the result is never reused |
| BI and reporting dashboards | Only show the axes someone chose in advance; a new question means a new development request |
| Vector RAG chatbots | Get relational and structural questions (multi-hop, cross-system) **plausibly wrong**, and cannot show the path that produced the answer |

The third one is where saem sits — and that position is two separate statements.

**Why it exists.** An ontology contract raises accuracy and turns failure into an **error**
rather than a plausible wrong answer. Refusing to answer without grounding is how that gets
enforced; give it up and saem is an expensive RAG.

**What is still empty.** Sitting read-only on top of existing line-of-business systems,
attaching grounding to every answer, and respecting source-system permissions — we have not
found a product that does all three. **That is an empty seat, not a proven advantage**: of the
three, the permission axis is still unsolved.

> *Re-checked 2026-08-24: newly released control-plane, agent-building, and MCP-integration
> features from several graph database and ontology-platform vendors were reviewed against this
> claim, across two passes. None of them alter it — the tools reviewed operate on data already
> loaded into their own managed store, with access scoped to that store's own accounts, rather
> than live, per-end-user delegated queries back to the original source systems that enforce
> that system's own row- and field-level permissions. This remains an observation to re-verify
> periodically, not a permanent fact.*
>
> *Re-checked 2026-09-06: three further 2026 entrants — a cloud data platform's cross-source
> shortcut feature, an enterprise search assistant, and an ontology platform's MCP endpoint — each
> evaluate access against their own store or a mirrored copy of the source's ACL (a configured
> connection credential, an ACL mirrored at crawl time and evaluated at query time, the platform's
> internal permissions). None runs the query back to the source system as the end user. The seat
> is still empty. **Re-check by 2026-12-06.** A claim about who else is in the market is true on the
> day it is made and quietly stops being true afterwards; the date says when this one has to be
> looked at again rather than leaving "periodically" to whoever remembers. One thing the same review did establish: pass-through session credentials,
> constrained delegation and on-behalf-of flows are standard features of established data
> virtualization products, so the delegation this project assumes is an adoption, not an
> invention — with a documented cost, one connection pool per user, that the assumptions above
> now carry.*

## Principles

The boundaries below, and those under [What saem is not](#what-saem-is-not), are also stated as
numbered clauses with an amendment procedure in [docs/CONSTITUTION.md](docs/CONSTITUTION.md).

**saem does not reimplement authorization — but it enforces it at two points.**
This one comes first because it is a boundary rather than a trade-off: the principles after it
are weighed against each other, and explainability is the first priority *inside* this line, not
a reason to cross it. It is also the principle furthest from being implemented (see the status
note at the top).
What is materialized never passes through query-time federation, so delegating to the source
system at query time alone would leave the relationship graph outside the permission model.

| Point | Covers | How |
|---|---|---|
| Materialization | Graph, indexes, embeddings | Materialized per group profile; instance edges inherit source-record visibility |
| Query | Record bodies | Federated with the credentials of the person asking — or the person an AI is asking for; row- and field-level rules are enforced by the source system |

**Edges by group, bodies by user.** A documented residual: within a group, differences between
individuals are not reflected in edge visibility — group partitioning narrows that exposure but
does not eliminate it.

**Explainability is non-negotiable, and it is enforced by the contract — not by a prompt.**
A response is a structure of `{claim, sources[], path[]}`, and **a claim with no sources cannot be
expressed at all**. Natural language is a rendering of that structure. When a query returns
nothing, saem refuses without invoking the model. If a principle is non-negotiable, the format
must not be able to express a violation. This costs real coverage, real latency, and — deliberately —
**synthesis**: "defects on this line are trending up" is a claim that lives in no single record.
Where the consumer is itself a synthesizer — an agent — that cost becomes a division of labour:
saem vouches for the facts and the path to them; conclusions drawn across them are the
consumer's, and saem's contract does not extend to them.
(*Grounding* here is that citation — an answer traceable to the source records it was read from.
It is not what LLM-based biomedical extractors call *ontology-based grounding*, the normalization
of a mention to a reference-ontology term id; saem does no such lookup, and "grounded" in these
documents always means "traceable to records".)

**saem never writes to source systems.** It is read-only. When something must be recorded, it goes
into saem's own space, and that space is bounded by a **reducibility rule**: every ontology
instance node must reduce to at least one source record. Nodes that unify entities across systems
are references to several records, and concepts that exist only in the organization (a line, an
equipment group, a crew) may exist only as **groupings** — they carry no facts of their own, and
their grounding is their members. This is the data-model form of the same principle: no
ungrounded answers, no ungrounded nodes.

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
not build an agent loop. What that contract carries is the distinguishing part: a knowledge scope,
the grounding a claim must cite, and the authority the query runs under. **How it is delivered is
not** — saem owns an MCP **server** and the built-in chat surface consumes it through an MCP
**client**, but exposing an ontology over MCP is table stakes as of 2026 (see the re-check above:
every recent entrant does it). Read the protocol as the transport it is, not as the reason to
choose this. What it does buy is structural rather than aspirational: the built-in chat connects
over the same contract everyone else does, so "the built-in UI is a reference consumer" is a fact
about the wiring instead of a promise.

**The answer contract** (`Saem.Contracts`, no reference to the server) is what a consumer codes
against. A lookup starts at one entity, follows declared relations, and reads declared fields — the
caller composes it in the ontology's terms; no model sits inside saem's path. The start names its
entity by its key, or by a declared field and the value it holds. The answer is either facts, each
with the source records it was read from and the relations that reached it, or a refusal that says
where the walk stopped — or, when the start's value is held by several entities the person can see,
those entities (`candidates`: each key and the record it was read from) to start again from by key. A fact without a source cannot be built. A refusal never
says "does not exist": a source that enforces its own permissions returns no rows both for what is
absent and for what this person may not see, and saem cannot tell the two apart. An answer is not a
snapshot: each source record says when it was read (`readAt`), and a walk that crosses systems reads
them one after another. Every answer names the version of the ontology it was walked in
(`catalogVersion`), and `ContractSchema` gives the same shape as a JSON Schema — it holds the rules
the constructors hold, so a consumer validating against it refuses what the contract cannot build.
Where a table is a projection of documents kept elsewhere — a form's records, projected by Formbase —
and that projection is behind its documents, a source read from it says so (`projection`: `stale`,
`unverified`, or `notProjected`), and so does a refusal at it: the value is what the table holds, and
the documents may say more. The server asks the Formbase that projects the table
(`Saem:Sources:Projections:<system>`: its URL, and the form type each table is projected from) once
per table per lookup, over the deployment's own key, since the state is about the form type and not
about anyone's rows; a Formbase that cannot answer leaves it unsaid.

**The ontology a lookup walks** (`Saem.Ontology`) is a set of declarations: each entity type
stands on one table of one source system, with its key column, its fields and — where a row is
cited by something other than its key — the columns that name the record it is, and each relation
leads from a field of one type to the key of another. A relation between two systems is not a
foreign key — neither source holds it — so it exists only as a declaration. A type may say what kind
of value its key is (`keyKind`: `text`, the default, or `number`) and what kind each field holds
(`kinds`: `text`, `freeText`, `number`, `date`, `key`). A number key is compared as the integer it
spells wherever keys meet — a lookup's start, a field followed to the next entity, a relation walked
backwards, the edges built for groups — so a reference written `0007` reaches the entity keyed `7`. A
claim carries its field's declared kind (`fieldKind`), and the tool's description lists the kinds: a
`freeText` value is whatever a person typed, so a consumer can hold it apart from instructions. A lookup is checked
against the declarations before any source is asked: a type, relation or field the ontology does
not declare is a malformed question, answered as an error rather than as a refusal. The
declarations are read behind a catalog port; the first adapter reads a JSON file and is re-read on
every lookup. The walk itself (`Saem.Query`) reads one source row at a time through sessions opened
as the person the lookup is for, follows each relation by the value it read, and builds the answer
from those reads alone — where a read returns nothing, the lookup is refused at that hop, naming the
relation and the system.

A relation may also declare an `inverse` — its name walked backwards, from an entity to every entity
that leads to it (`owl:inverseOf`). No single read finds every row that names an entity, so a backward
step, which may only be a path's last, asks the edges materialized for the person's groups (below) which
entities lead there, then reads each of them as the person and keeps only the ones the source shows and
still says lead there: the edges find candidates, the answer is still the person's own reads. Each
group's partition is answered on its own and the hop names it. A person in no group the deployment keeps
edges for is refused for that reason (`noMaterializedPartition`), and no source is read.

Each partition such a step read is reported on the answer (`partitions`), with what it says beyond the
facts. `blocked`: the partition led to entities the person's own reads did not show — they changed since
the edges were built, or the person may not see them; saem cannot tell which, and says nothing of what or
how many. A refusal can carry it too, and then both hold: no row was visible, and the group knew of
something. `incomplete`: the partition was built without some of its source documents, so entities may be
missing — it is read over the edges that stand, since an edge only finds a candidate and a missing one can
leave an answer out but never put a wrong one in. A step is bounded: when a partition leads to more
candidates than one step may read (`Saem:Lookup:MaxCandidatesPerStep`, 50 by default), none is read and
the lookup is a tool error asking for a narrower one — a limit of saem's, not an answer about the sources.
The same limit bounds a start named by a value.

**Ontology authoring** combines standard domain packs, entity/relation proposals from
[Eyu](https://github.com/iyulab/Eyu) (the LLM-assisted extraction and mapping engine, consumed —
not built here), and human review. Changes flow as changesets through simulate → apply → rollback.
The first step of that path is `Saem.Authoring`: Eyu speaks of entities — this record is a supplier,
that work order names this item — and the ontology speaks of types, so `DeclarationDrafter` reads
the declarations off the records a proposal cites. A type stands on the table of the subject whose
records denote its entities, keyed by that subject's key field; a relation leaves its type through
the field that, in every proposed instance, holds the key of the entity it leads to. Declarations
already in force are never changed, and anything the proposal said that cannot be declared — a type
only mentioned, a relation two fields could carry — comes back as a note naming its records. The
draft is written in the catalog file's own form, existing declarations first, so the review is a
diff a person accepts or trims before the file is replaced. `DeclarationAuthoring` runs one such
pass end to end: it samples each subject through a connector, hands Eyu the types already declared
as names in use — so a kind the ontology has is proposed under its name, not a new one — and
returns the draft with the proposal and the records it cites. A type is its table, not its name: a
proposed type on a table a declared type already stands on is read as that type, whatever the proposal
called it. `AuthoringPass.ToReview()` writes the pass as a person reviews it — each added declaration
with the records it was drawn from, what could not be declared and why, what Eyu left out, and the
file that would replace the catalog.

`saem-author` (`src/Saem.Authoring.Cli`) runs that pass from a terminal, for an administrator: the
documents are read from a Formbase instance, the declarations in force from the catalog file, and
the subjects from a sources file that says where each one's rows stand for a lookup. Out come three
files, a review, a draft catalog and the pass's proposals; nothing else is written, and an existing file
is replaced only with `--overwrite`.

```sh
# sources.json: { "items": { "system": "forms", "table": "items", "keyField": "item_code", "record": ["fb_doc_id"] } }
SAEM_MODEL_API_KEY=… saem-author --formbase http://localhost:8080 --sources sources.json \
  --catalog ontology.json --model-endpoint https://models.example/v1 --model <name> --model-stream \
  --draft ontology.json --overwrite        # the review is then `git diff ontology.json` + review.md
```

The pass also keeps what it proposed (`--proposals`, default `proposals.json`), since the review edits the
draft. Once the review is in the catalog, `saem-author settle --proposals proposals.json --catalog
ontology.json --records settled.jsonl` adds one line per proposal to a file of settled records in the
form [Gil](https://github.com/iyulab/gil/blob/main/docs/settled-records.md) reads: whether the
declaration was taken, and as proposed or changed (`AcceptOther` · `CorrectOther` — the suggestion came
from the pass, not from Gil), with the pass as its batch. The records are kept before anything reads them:
how often proposals of each kind are taken as they were is what a calibrated promise about them would be
chosen from. A pass is recorded once; `--settled-by` keeps an opaque reviewer id, never a name. The lines
carry what was suggested, which Gil reads from its 0.27.0 (Rust `iyulab-gil` 0.9.0) on.

A model call that fails transiently — a dropped stream, a 5xx — is tried again (`--retries`, default
one), and the review lists every retried attempt. Secrets come from the environment
(`SAEM_MODEL_API_KEY`, and `SAEM_FORMBASE_TOKEN` when something in front of Formbase asks for one),
never from the command line. This is direct authoring: the draft is a reviewed edit of the catalog
file, not a changeset — nothing is simulated or applied against the sources.

An answer says whose view it is (`answeredFor`: the person it was run as, and the agent that asked for
them, if one did) — a refusal included, since what one person may not see is not what nobody may — so it
is not taken for anyone else's once passed on.

Each source an answer cites says when saem read it (`readAt`): every source is read at the moment the
lookup is asked, as the person, so that is the time the value is as of — an agent holding the answer
knows how old the fact is. A relation walked backwards is found through edges built ahead of time, and
its hop says when its group's edges were read from the sources (`partitionAsOf`): the entities it leads
to are read again as the person, so what is stated of them is current, but one that came to lead there
after that time has no edge yet and is not among the answers.

An answer cites the record it read: by default the row's key, and where a table's rows are copies of
records kept elsewhere, the columns a type declares as its `record` — joined with `/` when there are
several. A form's projected rows are keyed by the record's own key, so relations follow it, and cited
by the document they were projected from (`"record": ["fb_doc_id"]`); a subject's `SubjectSource`
carries that choice into the declarations it drafts.

**Model access** goes through a single provider-neutral entry point, so saem runs fully
on-premises against whatever inference an organization already operates — a local model farm or a
hosted provider. Local inference is available out of the box, not required.

## Connectors

A connector reads one system that already exists and is owned by someone else, and hands
[Eyu](https://github.com/iyulab/Eyu) what it judges from: what the system declares about a subject
(Eyu's `IStructureSource`) and the records it holds (`IRecordSample`). The ports are Eyu's public
contract, not saem's, so a connector could be lifted out without being rewritten.

- **One source per connector, read-only.** A connector never writes, and never infers — entities and
  relations are Eyu's to propose from what it returns.
- **Across the process boundary.** A connector talks to its source over the source's own interface;
  it does not load the source's engine or read its storage.

`Saem.Connectors.Formbase` is the first: it reads a [Formbase](https://github.com/iyulab/Formbase)
instance over HTTP — a form type's declaration as declared structure, and its raw document stream as
records, so fields nobody has declared yet reach Eyu too. It needs Formbase 0.11.0 or later, the
first release that serves `GET /formtypes/{type}/documents`; an older instance is named as such
rather than answered with a bare status.

The records are records, not appends. Where documents name the record they belong to — a correction
is a new document under the same record key, and a retirement takes the record out — the connector
keeps each record's latest document and drops retired ones, the same fold Formbase's own projection
applies, so a corrected record reaches Eyu once. Documents without a key are records of their own, which
is every document on an instance that predates record keys.

A repeated section — the lines of an order sheet, declared as a form type of its own — has no documents:
its records are cut from the parent's, one per item of the section array, carrying the parent's key
field and named by the parent document's id, a slash and the item's position, the name Formbase gives
the projected row. Its declared structure leads back to the parent through that field, and the parent's
records leave the array out. Finding the parent from the section needs Formbase 0.33.0 or later, whose
section declaration names it (`sectionOf`); on an older instance a section reads as a form type with
no documents.

Where the instance is not the only Formbase on its PostgreSQL and MorphDB, pass its namespace
(`new FormbaseConnector(http, formbaseNamespace: "…")`, sent as `Formbase-Namespace`): a connector
pointed at the wrong host then fails with `404` instead of reading someone else's documents. The
namespace only checks which host answered — it does not keep data apart. Formbase hosts sharing one
PostgreSQL and one MorphDB need a schema and a project each (`Formbase__Schema`,
`Formbase__MorphDb__ProjectId`); hosts that differ only in namespace serve the same data.

```bash
dotnet test --solution saem.slnx

# the live test reads a running Formbase; without one it stops and says the URL is not a Formbase host
docker run -d -p 8080:8080 ghcr.io/iyulab/formbase:0.34.0
SAEM_FORMBASE_URL=http://127.0.0.1:8080 dotnet test --solution saem.slnx -p:IncludeFormbaseLiveTests=true
```

### Query-time sources

A lookup reads its sources at the moment it is asked, through `ISourceSessions`: a session per
system, opened as the person the lookup is for, so the source — its grants and its row-level rules —
decides what comes back, and a row the person may not see is simply not there. `PersonSessions`
binds one person's login for each system the ontology names; it is made per lookup and dropped with
it.

A session reads a row by key (`ReadAsync`, what a walk does at each hop) or every row of a table in key
order (`ScanAsync`) — a pass over a whole table, for building what a group may see ahead of a question.
Either way the source's rules decide which rows come back.

`Saem.Materialization` is that pass's first step: `EdgeMaterializer` reads, through one group's sessions,
the table of every type a relation leaves and gives an edge per row whose relation field holds a key — the
two entities' IRIs (`EntityIris`: the base, the type and the key, the same on every pass and with no group in
it) and the record the edge was read from, never the record's fields. A row the group may not see leaves no
edge. `FormbaseEdgeStore` keeps a group's edges in that group's own Formbase — one started over the group's
MorphDB project with a secret confined to it, so no other group's store or account reaches them. The edges
are one form type whose declaration has the edge's six text fields and nothing else; a pass sends only the
edges that changed, corrects an entity that now leads elsewhere, and retires the edges it no longer finds.
The server reads a person's groups from their token's `groups` claim and each group's store from
`Saem:Materialization` (the base IRI the pass named entities under, and each group's Formbase).

`saem-materialize` is the pass as a command a deployment schedules, one group per run: the catalog, a
sources file saying where each system is and which login the group's account uses, the group's store and
the base IRI. Each account's secret comes from the environment (`SAEM_SOURCE_SECRET_<SYSTEM>`), so a run
holds one group's secrets only. A system the relations need but the sources file does not name is refused
before the store is touched; the run reports how many edges it wrote, left unchanged and retired.

`Saem.Connectors.Postgres` and `Saem.Connectors.MySql` are the first such sources. Where a database is
the system's interface, reading it over its own protocol, as the person, *is* talking to it over its
own interface: the session logs in with the person's credential (`PostgresSource.As(user, secret)`,
`MySqlSource.As(user, secret)` — the secret asked for when the session opens and not kept), runs in a
read-only transaction rolled back when the session ends, and pools nothing between lookups. A
connection string that names a login is refused, and so is a PostgreSQL session that logged in as a
superuser or a role with `BYPASSRLS` (`InsecureSourceRoleException`): row-level security does not apply
to such a role, so the source would hand over every row instead of deciding what the person reads. One lookup can cross both: a work order in a
PostgreSQL MES names an item in a MySQL ERP, which names its supplier — and each person's answer, or
the hop at which it is refused, follows from what each source lets that person see.

```csharp
var mes = new PostgresSource("Host=mes;Database=postgres;SSL Mode=Require");
var sessions = new PersonSessions(new Dictionary<string, SourceOpener> { ["mes"] = mes.As("alice", ct => GetSecretAsync(ct)) });
var answer = await new LookupWalker(catalog).RunAsync(call, sessions, ct);
```

The credential is a password for now. Both sources accept an OpenID Connect token as a login
(PostgreSQL 18's OAuth, Percona Server's `auth_openid_connect`), which is how a person's own token would
reach them, but neither .NET driver can send one yet; when they can, the secret becomes the token and
nothing else in the path changes.

`Saem.Connectors.MorphDb` reads a [MorphDB](https://github.com/iyulab/MorphDB) project the same way,
with the person's token already: `MorphDbSource(http, projectId).As(token)` presents the token on every
read of its REST API (`GET`, filtered by key), MorphDB checks it against the issuer it trusts, and its
row-level security, evaluated as that person, decides which rows come back. The token is one obtained
for MorphDB's audience on that person's behalf — not the token saem was called with — and is asked for
when the session opens. A client that already carries a credential is refused. A refused read
(`401`, `403`, an unknown table) is an error; a row the person may not see is an empty answer.

```csharp
var forms = new MorphDbSource(new HttpClient { BaseAddress = new Uri("https://morphdb.internal/") }, projectId);
var sessions = new PersonSessions(new Dictionary<string, SourceOpener> { ["forms"] = forms.As(ct => ExchangeForMorphDbAsync(ct)) });
```

### MCP server

`Saem.Mcp` serves one tool, `lookup`, over MCP's Streamable HTTP transport. Its input is a lookup in
the ontology's terms — `start` (type, and key or `where`: a field and its value), `path` (relations),
`read` (fields) — and its result is
the answer contract above, as structured content and as the same JSON in text, declared as the tool's
output schema. The tool's description and input schema are built from the ontology when tools are
listed, so the vocabulary a caller sees is the one its call is checked against, and the listing names
the ontology's version in its `_meta` (`saem/catalogVersion`) — the version every answer carries. A call
outside that vocabulary is a tool error, not a refusal. A lookup still running at its deadline
(`Saem:Lookup:Deadline`, 10 seconds by default) is stopped and reported as not answered — a failure,
like a source that fails, never a refusal.

Who a lookup runs as is never an argument. Every request carries a bearer token from an issuer the
deployment trusts; the server validates it (signature, lifetime, audience) and runs the lookup as the
token's `sub`, recording the agent named in its `act` claim when an agent obtained the token on the
person's behalf (RFC 8693). A request without a valid token gets `401` with a pointer to the issuer
(RFC 9728 protected resource metadata). Each person then logs in to each source the way that source
can. A MorphDB source takes the person's token itself: the server exchanges the token it was called
with for one meant for that source (RFC 8693 token exchange at the same issuer, as saem's own client),
and the source checks it. PostgreSQL and MySQL sources take a password login configured per person,
until their drivers can present a token. The token the request presented is kept only for the
exchange and never logged.

Each lookup gets an id — the request's W3C trace id when it has one — which the server logs with the
person and the agent, and which each source session carries as its name (`application_name=saem/<id>`
on PostgreSQL, the `program_name` connection attribute on MySQL). The source's own record then shows,
for that id, which login it authorised — PostgreSQL's connection log, or on MySQL an audit log that
keeps connection attributes (the rig turns on Percona's `audit_log_filter` for this): the record that a
lookup ran as the person comes from the source, not only from saem.

```json
{
  "Saem": {
    "Ontology": "ontology.json",
    "Identity": { "Authority": "https://issuer.example/realm", "Audience": "saem" },
    "Sources": {
      "Postgres": { "mes": "Host=mes;Database=postgres;SSL Mode=Require" },
      "MySql": { "erp": "Server=erp;Database=erp;SslMode=Required" },
      "MorphDb": { "forms": { "Url": "https://forms.example/", "ProjectId": "…", "Audience": "morphdb" } },
      "TokenExchange": { "TokenEndpoint": "https://issuer.example/realm/token", "ClientId": "saem", "ClientSecret": "…" },
      "PasswordLogins": {
        "mes": { "alice": { "User": "alice_login", "Password": "…" } },
        "erp": { "alice": { "User": "alice_login", "Password": "…" } }
      }
    }
  }
}
```

```bash
# the live tests read a PostgreSQL and a MySQL that each enforce their own rules for two people, and
# ask the rig's issuer for each person's token to call the MCP server with
docker compose -f tests/rig/compose.yaml -f tests/rig/compose.password-login.yaml up -d --build --wait
SAEM_RIG_MES="Host=127.0.0.1;Port=55432;Database=postgres;SSL Mode=Require" \
SAEM_RIG_ERP="Server=127.0.0.1;Port=53306;Database=erp;SslMode=Required" \
SAEM_RIG_ISSUER="http://127.0.0.1:58080/rig" \
  dotnet test --solution saem.slnx -p:IncludeRigLiveTests=true
docker compose -f tests/rig/compose.yaml -f tests/rig/compose.password-login.yaml down -v
```

```bash
# the forms source: a MorphDB service that trusts the rig's issuer; saem exchanges each person's token
# for one meant for it. The tests that draft their ontology from the forms with Eyu first call a model,
# and the repeated-section one needs a Formbase image that names a section's parent (SAEM_RIG_FORMBASE_IMAGE)
docker compose -f tests/rig/compose.yaml -f tests/rig/compose.morphdb.yaml up -d --wait issuer forms
SAEM_RIG_ISSUER="http://127.0.0.1:58080/rig" SAEM_RIG_FORMS="http://127.0.0.1:58088/" \
SAEM_RIG_FORMS_MASTER_SECRET=rig-forms-master-local-only \
EYU_LLM_ENDPOINT=… EYU_LLM_API_KEY=… EYU_LLM_MODEL=… \
  dotnet test --solution saem.slnx -p:IncludeMorphDbLiveTests=true
docker compose -f tests/rig/compose.yaml -f tests/rig/compose.morphdb.yaml down -v
```

## Roadmap

Order, not dates.

**Done on the rig: query-time delegation.** Two independent relational sources that enforce their
own row-level rules, two users whose visibility differs, one question that crosses both systems — and
two different answers, each fact citing only records that user can read, each lookup shown in the log
to have run as that user. It runs over PostgreSQL and MySQL with per-person password logins, and over
a MorphDB source with the person's own token — exchanged for one meant for the source, which checks it
and applies its row-level security as that person. The relational sources take the token once their
drivers can present one.

**Done on the rig: one chain from a form to an answer.** A form document accepted by Formbase and
projected into MorphDB is answered through the MCP server with the document's own id as the record its
source names — and in one run, the ontology that answer is asked in is the draft an authoring pass made
from the forms, unedited, the relation followed included.

**Started: materialization by group.** Each group's edges are read through that group's own account and
kept in that group's own store; a person reads only the partitions of the groups their token names, a
relation walked backwards answers from them, and a person in no such group is refused. Paths longer
than one backward hop, embeddings, and how to show a path whose middle hop the user may not read stay
open.

What these slices do not show is part of the plan. They run on synthetic fixtures, so they say nothing
about whether a given legacy system can be queried as its end users at all. Across the two relational
systems the relations are declared by hand, not proposed by Eyu.

**Next: a real organization's systems.** Whether people who work across those systems need it, and
whether a lookup answered as each of them is fast enough, are measured there, not on the rig — starting
with the cost the delegation model already carries on paper: one connection pool per user.

## Consumption modes

saem is self-contained and can also be integrated as middleware. All three modes run over the same
contract — the contract is the first-class output, and saem's own surfaces consume it the way
anyone else does:

1. **API / MCP** — a host, an agent, or a generated application brings its own UI and reads
   grounded context from saem
2. **Client-side widget embedding** — a host application embeds saem's chat or `saem-artifact` widgets
3. **Standalone** — an organization runs saem and manages and queries the ontology inside it

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

- [Eyu](https://github.com/iyulab/Eyu) — the ontology inference engine; saem hands it declared
  structure and raw records, and materializes what it proposes
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

The contract a consumer compiles into its own code — `src/Saem.Contracts`, the shape of a lookup
and its answer — is MIT, declared by the `LICENSE` in that directory and by its package. Calling
saem over its API or MCP surface does not make the caller a derivative of the server; the contract
is the one piece a consumer links. The CLA covers every path in the repository alike.
