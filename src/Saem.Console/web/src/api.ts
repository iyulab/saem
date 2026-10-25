// The console's API, as the browser reaches it: same origin, the session cookie only — the browser never holds a
// token — and the console's header on every request that changes something (the console refuses one without it).

export const RequestHeader = "X-Saem-Console";

export type Caller = { subject: string; roles: string[] };

export type Declaration = { kind: string; name: string } & Record<string, unknown>;

export type Catalog = { version: string; declarations: Declaration[] };

export type GraphNode = { name: string; kind: "entityType" | "grouping"; system: string | null; degree: number };

export type GraphEdge = {
  from: string;
  to: string;
  kind: "relation" | "identity" | "member";
  name: string;
  via?: string | null;
  inverse?: string | null;
  linkage?: string | null;
};

export type DeclarationGraph = { version: string; nodes: GraphNode[]; edges: GraphEdge[] };

export type CheckSummary = { checkedAt: string | null; catalogVersion: string | null; current: boolean; holds: boolean; violations: number };

export type Overview = { catalogVersion: string; declarations: Record<string, number>; check: CheckSummary | null };

export type CheckFinding = { type: string; relation: string | null; kind: string; violation: boolean; what: string; count: number; recordsCited: number };

export type CheckDifference = { type: string; relation: string | null; kind: string; before: number | null; after: number | null; worse: boolean };

export type CheckReport = {
  checkedAt: string;
  catalogVersion: string;
  holds: boolean;
  findings: CheckFinding[];
  against?: { catalogVersion: string; worse: number; differences: CheckDifference[] };
};

export type EntityKey = { type: string; key: string };

export type SourceRecord = { system: string; table: string; record: string; readAt?: string };

export type Hop = { relation: string; from: EntityKey; to: EntityKey; partition?: string };

export type Claim = { entity: EntityKey; field: string; value: unknown; sources: SourceRecord[]; path: Hop[]; fieldKind?: string };

export type Refusal = { reason: string; atHop: number; relation?: string | null; system: string };

export type PartitionRead = { name: string; asOf?: string | null; blocked: boolean; incomplete: boolean };

/** The lookup tool's answer, as the MCP server gives it to the person looking — the console passes it on unchanged. */
export type LookupAnswer = { catalogVersion?: string; partitions?: PartitionRead[]; answeredFor?: { subject: string; actedBy?: string } } & (
  | { kind: "answered"; claims: Claim[] }
  | { kind: "refused"; refusal: Refusal }
  | { kind: "candidates"; entities: Array<{ entity: EntityKey; source: SourceRecord }> }
);

/** Where a lookup starts: an entity by its key, or by the value one of its declared fields holds. */
export type LookupStart = EntityKey | { type: string; where: { field: string; value: string } };

export type LookupCall = { start: LookupStart; path: string[]; read: string[] };

export type JobKind = "verify" | "check" | "pass" | "changed-pass" | "fit" | "judge";

/** One job of a group's executor, as the console gives it: a check's lines are withheld and its report has no records. */
export type Job = {
  id: string;
  kind: JobKind;
  requestedBy: string;
  arguments: { identity?: string | null; first?: string | null; second?: string | null; same?: boolean | null };
  state: "running" | "succeeded" | "failed" | "cancelled";
  exitCode: number | null;
  output: string;
  outputTruncated: boolean;
  outputWithheld?: boolean;
  startedAt: string;
  finishedAt: string | null;
  report: CheckReport | null;
};

/** What the console answered: the value, or why there is none — signed out, no console role, or a problem it named. */
export type Answer<T> =
  | { ok: true; value: T }
  | { ok: false; status: 401 | 403 }
  | { ok: false; status: number; problem: string };

async function ask<T>(path: string, init?: RequestInit): Promise<Answer<T>> {
  const response = await fetch(path, { credentials: "same-origin", ...init });
  if (response.ok) {
    return { ok: true, value: (response.status === 204 ? undefined : await response.json()) as T };
  }
  if (response.status === 401 || response.status === 403) {
    return { ok: false, status: response.status };
  }
  // A problem the console names (RFC 9457) says why; anything else says only its status.
  const problem = await response
    .json()
    .then((p: { title?: string; detail?: string; error?: string }) => [p.title, p.detail, p.error].filter(Boolean).join(" "))
    .catch(() => "");
  return { ok: false, status: response.status, problem: problem || `The console answered ${response.status}.` };
}

const change = (body?: unknown): RequestInit => ({
  method: "POST",
  headers: { [RequestHeader]: "1", ...(body === undefined ? {} : { "Content-Type": "application/json" }) },
  body: body === undefined ? undefined : JSON.stringify(body),
});

export const api = {
  me: () => ask<Caller>("/api/me"),
  overview: () => ask<Overview>("/api/overview"),
  check: () => ask<CheckReport>("/api/check"),
  catalog: () => ask<Catalog>("/api/catalog"),
  graph: () => ask<DeclarationGraph>("/api/catalog/graph"),
  logout: () => ask<void>("/logout", change()),
  executors: () => ask<Array<{ group: string }>>("/api/executors"),
  jobs: (group: string) => ask<Job[]>(`/api/executors/${encodeURIComponent(group)}/jobs`),
  startJob: (group: string, kind: JobKind) => ask<Job>(`/api/executors/${encodeURIComponent(group)}/jobs`, change({ kind })),
  cancelJob: (group: string, id: string) => ask<void>(`/api/executors/${encodeURIComponent(group)}/jobs/${id}/cancel`, change()),
  lookup: (call: LookupCall) => ask<LookupAnswer>("/api/graph/local", change(call)),
};

/** Where signing in starts: the console sends the browser to the issuer and back to this page. */
export const signInAddress = (returnTo: string) => `/login?returnUrl=${encodeURIComponent(returnTo)}`;
