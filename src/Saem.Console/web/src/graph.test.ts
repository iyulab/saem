import { describe, expect, it } from "vitest";
import type { DeclarationGraph } from "./api";
import { elements, neighborhood, sizeOf, systems } from "./graph";

const graph: DeclarationGraph = {
  version: "v1",
  nodes: [
    { name: "WorkOrder", kind: "entityType", system: "mes", degree: 2 },
    { name: "Item", kind: "entityType", system: "erp", degree: 1 },
    { name: "Plant", kind: "grouping", system: null, degree: 1 },
  ],
  edges: [
    { from: "WorkOrder", to: "Item", kind: "relation", name: "orders_item", via: "item" },
    { from: "WorkOrder", to: "Plant", kind: "member", name: "Plant", via: "plant" },
  ],
};

describe("the declaration graph, drawn", () => {
  it("colors each system the same way every time, in name order, and leaves groupings uncolored by system", () => {
    expect(systems(graph).map((s) => s.system)).toEqual(["erp", "mes"]);
    const nodes = elements(graph).filter((e) => e.group === "nodes");
    const color = (id: string) => nodes.find((n) => n.data.id === id)!.data;
    expect(color("Item").color).toBe(systems(graph)[0].color);
    expect(color("Plant").system).toBeNull();
  });

  it("sizes a node by its degree, growing slowly", () => {
    expect(sizeOf(0)).toBe(24);
    expect(sizeOf(4)).toBe(44);
    expect(sizeOf(16)).toBeLessThan(sizeOf(4) * 2);
  });

  it("gives every edge an id of its own and labels a membership by the field naming the group", () => {
    const edges = elements(graph).filter((e) => e.group === "edges");
    expect(new Set(edges.map((e) => e.data.id)).size).toBe(edges.length);
    expect(edges.find((e) => e.data.kind === "member")!.data.label).toBe("plant");
  });

  it("lists what touches a node with the node at the other end", () => {
    expect(neighborhood(graph, "Item")).toEqual([{ edge: graph.edges[0], other: "WorkOrder", outgoing: false }]);
  });
});
