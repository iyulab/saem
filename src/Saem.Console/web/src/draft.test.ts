import { describe, expect, it } from "vitest";
import type { CatalogDraft } from "./api";
import { changesAnything, mayConfirm, nextStep, settlementLine, split } from "./draft";

const draft = (changes: CatalogDraft["changes"], same = false): CatalogDraft => ({
  inForce: "aaaa",
  draft: same ? "aaaa" : "bbbb",
  changes,
  breaking: changes.filter((c) => c.kind === "breaking").length,
});

describe("what a confirmation says of the reviewed pass", () => {
  it("counts what the review took and what it did not, and says nothing when reviews are not recorded", () => {
    expect(settlementLine({ pass: "pass-1", recorded: true, proposals: 3, taken: 2 })).toBe("Settled 3 proposals of pass-1: 2 taken, 1 not.");
    expect(settlementLine({ pass: "pass-1", recorded: false, proposals: 3, taken: 0 })).toBe("pass-1 was settled by an earlier confirmation.");
    expect(settlementLine(null)).toBeNull();
  });
});

describe("a draft against the catalog in force", () => {
  it("separates what can break from what only adds", () => {
    const parts = split(
      draft([
        { kind: "breaking", declaration: "relation on_line", change: "removed" },
        { kind: "additive", declaration: "entityType Shift", change: "added" },
      ]),
    );

    expect(parts.breaking.map((c) => c.declaration)).toEqual(["relation on_line"]);
    expect(parts.additive.map((c) => c.declaration)).toEqual(["entityType Shift"]);
  });

  it("lets a builder confirm only a draft that changes something, with breaking changes acknowledged", () => {
    const additive = draft([{ kind: "additive", declaration: "entityType Shift", change: "added" }]);
    const breaking = draft([{ kind: "breaking", declaration: "relation on_line", change: "removed" }]);

    expect(mayConfirm(additive, false)).toBe(true);
    expect(mayConfirm(breaking, false)).toBe(false);
    expect(mayConfirm(breaking, true)).toBe(true);
    expect(changesAnything(draft([], true))).toBe(false);
    expect(mayConfirm(draft([], true), true)).toBe(false);
  });

  it("tells what to do after each refusal", () => {
    expect(nextStep("stale-base")).toBe("read-again");
    expect(nextStep("stale-draft")).toBe("read-again");
    expect(nextStep("nothing-to-confirm")).toBe("read-again");
    expect(nextStep("breaking")).toBe("acknowledge");
    expect(nextStep(undefined)).toBe("none");
  });
});
