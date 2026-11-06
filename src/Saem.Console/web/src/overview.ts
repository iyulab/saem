import { kinds } from "./catalog";

/** What a catalog declares as one sentence a person reads: "2 kinds of things and 1 link", or that it declares nothing yet. */
export function declaredSentence(declarations: Record<string, number>): string {
  const words: Record<string, [string, string]> = {
    entityType: ["kind of thing", "kinds of things"],
    relation: ["link", "links"],
    identity: ["same-thing rule", "same-thing rules"],
    linkage: ["matching rule", "matching rules"],
    grouping: ["group", "groups"],
  };
  const parts = kinds
    .filter((k) => (declarations[k.kind] ?? 0) > 0)
    .map((k) => `${declarations[k.kind]} ${declarations[k.kind] === 1 ? words[k.kind][0] : words[k.kind][1]}`);
  if (parts.length === 0) return "Nothing is declared yet";
  return parts.length === 1 ? parts[0] : `${parts.slice(0, -1).join(", ")} and ${parts[parts.length - 1]}`;
}
