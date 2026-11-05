import type { EntityKey, LookupAnswer } from "./api";
import { idOf, refusalText } from "./localGraph";

/** What reading one side of a pair as the person judging gave: the values they may read, or why they read nothing. */
export type Side = { kind: "read"; values: ReadonlyMap<string, unknown> } | { kind: "nothing"; why: string };

/** One declared field, read on each side: a side that gave no value for it holds null — empty, or not readable by this person. */
export type CompareRow = { field: string; one: string | null; other: string | null; verdict: "same" | "different" | "one side only" };

/** A value as a person reads it in a cell. */
export const shown = (value: unknown): string => (typeof value === "string" ? value : JSON.stringify(value));

/** The values a lookup of one entity gave on that entity, or the reason it gave none — a refusal is kept as such, never turned into "empty". */
export function sideOf(answer: LookupAnswer, entity: EntityKey): Side {
  if (answer.kind === "refused") {
    return { kind: "nothing", why: refusalText(answer.refusal) };
  }
  if (answer.kind === "candidates") {
    return { kind: "nothing", why: "More than one entity answers to that start; read it by its key." };
  }
  const values = new Map<string, unknown>();
  for (const claim of answer.claims) {
    if (idOf(claim.entity) === idOf(entity) && !values.has(claim.field)) {
      values.set(claim.field, claim.value);
    }
  }
  return { kind: "read", values };
}

/** The two sides' fields in name order, with whether the values agree — what a person judging "is it one thing?" reads across. */
export function compare(fields: readonly string[], one: Side, other: Side): CompareRow[] {
  const read = (side: Side, field: string) => (side.kind === "read" && side.values.has(field) ? shown(side.values.get(field)) : null);
  return [...new Set(fields)]
    .sort((a, b) => a.localeCompare(b))
    .map((field) => {
      const a = read(one, field);
      const b = read(other, field);
      return { field, one: a, other: b, verdict: a === null || b === null ? "one side only" : a === b ? "same" : "different" };
    });
}
