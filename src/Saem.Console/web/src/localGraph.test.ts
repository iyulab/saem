import { describe, expect, it } from "vitest";
import type { Catalog, LookupAnswer } from "./api";
import { empty, fieldsOf, idOf, refusalText, step, walksFrom } from "./localGraph";

const catalog: Catalog = {
  version: "v1",
  declarations: [
    { kind: "entityType", name: "WorkOrder", system: "mes", fields: { note: "note", item: "item_code" } },
    { kind: "entityType", name: "Item", system: "erp", fields: { name: "item_name" } },
    { kind: "relation", name: "orders_item", from: "WorkOrder", via: "item", to: "Item", inverse: "ordered_by" },
  ],
};

const source = { system: "mes", table: "work_orders", record: "WO-3" };
const wo = { type: "WorkOrder", key: "WO-3" };

describe("walking a local graph", () => {
  it("offers each relation leaving a type, and each arriving one by its inverse", () => {
    expect(walksFrom(catalog, "WorkOrder")).toEqual([{ relation: "orders_item", to: "Item", backwards: false }]);
    expect(walksFrom(catalog, "Item")).toEqual([{ relation: "ordered_by", to: "WorkOrder", backwards: true }]);
    expect(fieldsOf(catalog, "WorkOrder")).toEqual(["item", "note"]);
  });

  it("adds what a step reached with an edge from where it walked, one level deeper", () => {
    const start: LookupAnswer = { kind: "answered", claims: [{ entity: wo, field: "note", value: "hello", sources: [source], path: [] }] };
    const walked: LookupAnswer = {
      kind: "answered",
      claims: [
        { entity: { type: "Item", key: "I-1" }, field: "name", value: "bolt", sources: [{ system: "erp", table: "items", record: "I-1" }], path: [] },
        { entity: { type: "Item", key: "I-2" }, field: "name", value: "nut", sources: [{ system: "erp", table: "items", record: "I-2" }], path: [] },
      ],
    };

    const graph = step(step(empty(), wo, null, start), wo, "orders_item", walked);

    expect([...graph.nodes.keys()]).toEqual(["WorkOrder/WO-3", "Item/I-1", "Item/I-2"]);
    expect(graph.nodes.get("Item/I-1")!.depth).toBe(1);
    expect(graph.edges).toEqual([
      { from: "WorkOrder/WO-3", to: "Item/I-1", relation: "orders_item" },
      { from: "WorkOrder/WO-3", to: "Item/I-2", relation: "orders_item" },
    ]);
    expect(graph.steps[1].reached).toEqual(["Item/I-1", "Item/I-2"]);
  });

  it("keeps a refusal as the step's answer, adding nothing, and says it is about what the person can see", () => {
    const refused: LookupAnswer = { kind: "refused", refusal: { reason: "noVisibleResult", atHop: 1, relation: "orders_item", system: "erp" }, partitions: [] };

    const graph = step(empty(), wo, "orders_item", refused);

    expect(graph.nodes.size).toBe(0);
    expect(graph.steps[0].refusal?.system).toBe("erp");
    expect(refusalText(graph.steps[0].refusal!)).toContain("may hold rows your account cannot read");
    expect(idOf(wo)).toBe("WorkOrder/WO-3");
  });
});
