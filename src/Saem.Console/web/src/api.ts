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
    .then((p: { title?: string; detail?: string }) => [p.title, p.detail].filter(Boolean).join(" "))
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
  catalog: () => ask<Catalog>("/api/catalog"),
  graph: () => ask<DeclarationGraph>("/api/catalog/graph"),
  logout: () => ask<void>("/logout", change()),
};

/** Where signing in starts: the console sends the browser to the issuer and back to this page. */
export const signInAddress = (returnTo: string) => `/login?returnUrl=${encodeURIComponent(returnTo)}`;
