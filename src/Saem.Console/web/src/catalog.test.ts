import { describe, expect, it } from "vitest";
import { askExample, byKind, summary } from "./catalog";

describe("the catalog, kind by kind", () => {
  it("keeps the catalog file's order of kinds, drops empty ones, and puts an unknown kind last", () => {
    const sections = byKind([
      { kind: "relation", name: "orders_item", from: "WorkOrder", via: "item", to: "Item" },
      { kind: "future", name: "x" },
      { kind: "entityType", name: "WorkOrder", system: "mes", table: "work_orders" },
    ]);

    expect(sections.map((s) => s.kind)).toEqual(["entityType", "relation", "future"]);
    expect(sections[2].title).toBe("future");
  });

  it("says what a declaration is from its own fields", () => {
    expect(summary({ kind: "entityType", name: "WorkOrder", system: "mes", table: "work_orders" })).toBe("mes · work_orders");
    expect(summary({ kind: "relation", name: "r", from: "WorkOrder", via: "item", to: "Item" })).toBe("WorkOrder —item→ Item");
    expect(summary({ kind: "grouping", name: "g" })).toBe("");
  });
});

describe("a lookup to ask the catalog", () => {
  const items = { kind: "entityType", name: "items", system: "forms", table: "items", fields: { item_name: "item_name", owner: "owner" } };
  const orders = { kind: "entityType", name: "work_orders", system: "forms", table: "work_orders", fields: { qty: "qty" } };

  it("starts where a link starts, follows it, and reads a field of where it ends", () => {
    const relation = { kind: "relation", name: "item", from: "work_orders", via: "item_code", to: "items" };

    expect(askExample([items, orders, relation])).toEqual({ start: { type: "work_orders", key: "the key of one row" }, path: ["item"], read: ["item_name"] });
  });

  it("reads a field of the first kind of thing when nothing is linked", () => {
    expect(askExample([items])).toEqual({ start: { type: "items", key: "the key of one row" }, path: [], read: ["item_name"] });
  });

  it("offers nothing when no kind of thing is declared", () => {
    expect(askExample([])).toBeNull();
    expect(askExample([{ kind: "relation", name: "r", from: "a", to: "b" }])).toBeNull();
  });
});
