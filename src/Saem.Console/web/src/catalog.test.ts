import { describe, expect, it } from "vitest";
import { byKind, summary } from "./catalog";

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
