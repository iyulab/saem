import type { Declaration } from "./api";

/** The kinds a catalog declares, in the order a catalog file lists them, with the name a builder reads. */
export const kinds: ReadonlyArray<{ kind: string; title: string }> = [
  { kind: "entityType", title: "Entity types" },
  { kind: "relation", title: "Relations" },
  { kind: "identity", title: "Identities" },
  { kind: "linkage", title: "Linkage rules" },
  { kind: "grouping", title: "Groupings" },
];

/** The declarations kind by kind, in the catalog's order; a kind this page does not know comes last, under its own name. */
export function byKind(declarations: readonly Declaration[]): Array<{ kind: string; title: string; declarations: Declaration[] }> {
  const known = new Set(kinds.map((k) => k.kind));
  const unknown = [...new Set(declarations.map((d) => d.kind).filter((k) => !known.has(k)))];
  return [...kinds, ...unknown.map((kind) => ({ kind, title: kind }))]
    .map((k) => ({ ...k, declarations: declarations.filter((d) => d.kind === k.kind) }))
    .filter((k) => k.declarations.length > 0);
}

/** One line saying what a declaration is, from its own fields — the system and table a type stands on, a relation's ends. */
export function summary(d: Declaration): string {
  const s = (key: string) => (typeof d[key] === "string" ? (d[key] as string) : undefined);
  switch (d.kind) {
    case "entityType":
      return [s("system"), s("table")].filter(Boolean).join(" · ");
    case "relation":
      return `${s("from")} —${s("via") ?? ""}→ ${s("to")}`;
    case "identity":
      return `${s("first")} = ${s("second")}`;
    default:
      return "";
  }
}

/**
 * A lookup that would work against these declarations, for a person who has never asked this catalog anything: it starts at a
 * kind of thing, follows a declared link when there is one, and reads a field of where it ends. Null when nothing is declared to ask.
 */
export function askExample(declarations: readonly Declaration[]): { start: { type: string; key: string }; path: string[]; read: string[] } | null {
  const types = declarations.filter((d) => d.kind === "entityType");
  const relation = declarations.find((d) => d.kind === "relation" && types.some((t) => t.name === d.from));
  const start = relation ? types.find((t) => t.name === relation.from) : types[0];
  if (!start) return null;
  const end = (relation && types.find((t) => t.name === relation.to)) || start;
  const fields = end.fields !== null && typeof end.fields === "object" ? Object.keys(end.fields as Record<string, unknown>) : [];
  return {
    start: { type: start.name, key: "the key of one row" },
    path: relation ? [relation.name] : [],
    read: fields.slice(0, 1),
  };
}
