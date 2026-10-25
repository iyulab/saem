import type { Catalog, Claim, EntityKey, LookupAnswer, PartitionRead, Refusal, SourceRecord } from "./api";

/** How far a local graph walks from where it started: three relations (saem D-88 C7). */
export const maxDepth = 3;

/** A relation a type can be walked along: forwards by its name, or backwards by its declared inverse. */
export type Walk = { relation: string; to: string; backwards: boolean };

type EntityTypeDeclaration = { kind: "entityType"; name: string; system: string; fields?: Record<string, string> };
type RelationDeclaration = { kind: "relation"; name: string; from: string; to: string; inverse?: string };

const entityTypes = (catalog: Catalog) => catalog.declarations.filter((d): d is EntityTypeDeclaration & Catalog["declarations"][number] => d.kind === "entityType");
const relations = (catalog: Catalog) => catalog.declarations.filter((d): d is RelationDeclaration & Catalog["declarations"][number] => d.kind === "relation");

/** The system a type stands on, as the catalog declares it. */
export const systemOf = (catalog: Catalog, type: string) => entityTypes(catalog).find((t) => t.name === type)?.system ?? null;

/** The fields a type declares, in name order — what a step reads on the entities it reaches. */
export const fieldsOf = (catalog: Catalog, type: string) => Object.keys(entityTypes(catalog).find((t) => t.name === type)?.fields ?? {}).sort();

/** Where an entity of a type can be walked: each relation leaving it, and each relation arriving at it that declares an inverse. */
export function walksFrom(catalog: Catalog, type: string): Walk[] {
  return [
    ...relations(catalog)
      .filter((r) => r.from === type)
      .map((r) => ({ relation: r.name, to: r.to, backwards: false })),
    ...relations(catalog)
      .filter((r) => r.to === type && typeof r.inverse === "string" && r.inverse.length > 0)
      .map((r) => ({ relation: r.inverse!, to: r.from, backwards: true })),
  ].sort((a, b) => a.relation.localeCompare(b.relation));
}

export const idOf = (e: EntityKey) => `${e.type}/${e.key}`;

/** An entity the walk reached, the values read on it with where each came from, and how many relations from the start. */
export type Visited = { entity: EntityKey; depth: number; values: Array<{ field: string; value: unknown; sources: SourceRecord[]; fieldKind?: string }> };

/** What one step found: the entities and edges it added, or the refusal it was answered with, and the partitions it read. */
export type Step = { from: string; relation: string; reached: string[]; refusal?: Refusal; partitions: PartitionRead[] };

export type LocalGraph = { nodes: Map<string, Visited>; edges: Array<{ from: string; to: string; relation: string }>; steps: Step[] };

export const empty = (): LocalGraph => ({ nodes: new Map(), edges: [], steps: [] });

function visit(nodes: Map<string, Visited>, claims: Claim[], depth: number): string[] {
  const reached: string[] = [];
  for (const claim of claims) {
    const id = idOf(claim.entity);
    const node = nodes.get(id) ?? { entity: claim.entity, depth, values: [] };
    if (!nodes.has(id)) {
      reached.push(id);
    }
    if (!node.values.some((v) => v.field === claim.field)) {
      node.values = [...node.values, { field: claim.field, value: claim.value, sources: claim.sources, fieldKind: claim.fieldKind }].sort((a, b) => a.field.localeCompare(b.field));
    }
    nodes.set(id, node);
  }
  return reached;
}

/**
 * The graph after one step: the start read with no relation, or a relation walked from a node already in the graph.
 * The answer is taken as the server gave it — a refusal is kept as the step's answer, never turned into "nothing there".
 */
export function step(graph: LocalGraph, from: EntityKey, relation: string | null, answer: LookupAnswer): LocalGraph {
  const nodes = new Map(graph.nodes);
  const depth = relation === null ? 0 : (nodes.get(idOf(from))?.depth ?? 0) + 1;
  const partitions = answer.partitions ?? [];
  if (answer.kind !== "answered") {
    const refusal = answer.kind === "refused" ? answer.refusal : undefined;
    return { nodes, edges: graph.edges, steps: [...graph.steps, { from: idOf(from), relation: relation ?? "", reached: [], refusal, partitions }] };
  }
  const reached = visit(nodes, answer.claims, depth);
  const edges = relation === null ? graph.edges : [...graph.edges];
  if (relation !== null) {
    for (const id of new Set(answer.claims.map((c) => idOf(c.entity)))) {
      if (!edges.some((e) => e.from === idOf(from) && e.to === id && e.relation === relation)) {
        edges.push({ from: idOf(from), to: id, relation });
      }
    }
  }
  return { nodes, edges, steps: [...graph.steps, { from: idOf(from), relation: relation ?? "", reached, partitions }] };
}

/** Why a step stated nothing, in the words the contract gives it: not "not found" — what this person can see. */
export function refusalText(refusal: Refusal): string {
  const where = refusal.relation ? `at ${refusal.relation} (step ${refusal.atHop})` : "at the start";
  return refusal.reason === "noMaterializedPartition"
    ? `Nothing to walk ${where}: you are in no group whose edges are kept for ${refusal.system}.`
    : `Nothing you can see ${where} in ${refusal.system}. The source may hold rows your account cannot read.`;
}
