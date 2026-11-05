import { describe, expect, it } from "vitest";
import type { LookupAnswer } from "./api";
import { compare, shown, sideOf } from "./compare";

const key = { type: "Supplier", key: "S1" };
const answered = (...claims: Array<[string, unknown]>): LookupAnswer => ({
  kind: "answered",
  claims: claims.map(([field, value]) => ({ entity: key, field, value, sources: [], path: [] })),
});

describe("reading the two sides of a pair side by side", () => {
  it("reads the values a lookup gave on the entity it started from", () => {
    const side = sideOf(answered(["name", "Acme"], ["city", "Busan"]), key);
    expect(side.kind).toBe("read");
    expect(side.kind === "read" && Object.fromEntries(side.values)).toEqual({ name: "Acme", city: "Busan" });
  });

  it("ignores a claim about another entity the lookup reached", () => {
    const answer: LookupAnswer = { kind: "answered", claims: [{ entity: { type: "Supplier", key: "S2" }, field: "name", value: "Other", sources: [], path: [] }] };
    const side = sideOf(answer, key);
    expect(side.kind === "read" && side.values.size).toBe(0);
  });

  it("keeps a refusal as the reason nothing was read, not as an empty side", () => {
    const side = sideOf({ kind: "refused", refusal: { reason: "noReadableRow", atHop: 0, system: "erp" } }, key);
    expect(side.kind).toBe("nothing");
    expect(side.kind === "nothing" && side.why).toContain("erp");
  });

  it("says, field by field in name order, whether the two values agree, differ or only one side has one", () => {
    const one = sideOf(answered(["name", "Acme"], ["city", "Busan"], ["tier", 1]), key);
    const other = sideOf(answered(["name", "Acme"], ["city", "Seoul"]), key);
    expect(compare(["tier", "name", "city", "name"], one, other)).toEqual([
      { field: "city", one: "Busan", other: "Seoul", verdict: "different" },
      { field: "name", one: "Acme", other: "Acme", verdict: "same" },
      { field: "tier", one: "1", other: null, verdict: "one side only" },
    ]);
  });

  it("holds a side that read nothing as no value on every field", () => {
    const one = sideOf(answered(["name", "Acme"]), key);
    const none = sideOf({ kind: "refused", refusal: { reason: "noReadableRow", atHop: 0, system: "erp" } }, key);
    expect(compare(["name"], one, none)).toEqual([{ field: "name", one: "Acme", other: null, verdict: "one side only" }]);
  });

  it("writes a value that is not text as JSON", () => {
    expect(shown("a")).toBe("a");
    expect(shown(3)).toBe("3");
    expect(shown({ x: 1 })).toBe('{"x":1}');
  });
});
