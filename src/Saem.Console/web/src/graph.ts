import type { DeclarationGraph, GraphEdge, GraphNode } from "./api";

/** A color per system, in name order, so the same catalog always draws the same; a grouping (no system) has none. */
const palette = ["#2f6fdf", "#d1572b", "#1f9d6b", "#9b4fd1", "#c2911b", "#2a9cb0", "#c2417d", "#6b7a1f"];

export type Element =
  | { group: "nodes"; data: { id: string; label: string; kind: GraphNode["kind"]; system: string | null; degree: number; size: number; color: string } }
  | { group: "edges"; data: { id: string; source: string; target: string; kind: GraphEdge["kind"]; label: string } };

/** Each system with its color, in name order — the same catalog colors its systems the same on every screen. */
export function systemColors(names: Iterable<string | null>): Array<{ system: string; color: string }> {
  return [...new Set([...names].filter((s): s is string => s !== null))]
    .sort()
    .map((system, i) => ({ system, color: palette[i % palette.length] }));
}

/** The systems the graph's types stand on, each with its color. */
export const systems = (graph: DeclarationGraph) => systemColors(graph.nodes.map((n) => n.system));

/** The color a node of no system is drawn in. */
export const noSystem = "#8a919b";

/** A node's size grows with how many edges touch it, slowly, so a hub stands out without hiding its neighbors. */
export const sizeOf = (degree: number) => Math.round(24 + 10 * Math.sqrt(degree));

/** The declaration graph as graph elements: a node per type and grouping, an edge per relation, identity and membership. */
export function elements(graph: DeclarationGraph): Element[] {
  const color = new Map(systems(graph).map((s) => [s.system, s.color]));
  const nodes: Element[] = graph.nodes.map((n) => ({
    group: "nodes",
    data: { id: n.name, label: n.name, kind: n.kind, system: n.system, degree: n.degree, size: sizeOf(n.degree), color: n.system ? color.get(n.system)! : noSystem },
  }));
  // Two edges may share a name (a grouping's memberships all carry the grouping's), so an edge's id is its kind, name and ends.
  const edges: Element[] = graph.edges.map((e) => ({
    group: "edges",
    data: { id: `${e.kind}:${e.name}:${e.from}->${e.to}`, source: e.from, target: e.to, kind: e.kind, label: e.kind === "member" ? (e.via ?? "") : e.name },
  }));
  return [...nodes, ...edges];
}

/** What touches a node: each edge with the node at the other end, so a builder reads a type's neighborhood in one list. */
export function neighborhood(graph: DeclarationGraph, name: string): Array<{ edge: GraphEdge; other: string; outgoing: boolean }> {
  return graph.edges
    .filter((e) => e.from === name || e.to === name)
    .map((edge) => ({ edge, other: edge.from === name ? edge.to : edge.from, outgoing: edge.from === name }));
}
