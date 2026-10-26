import { describe, expect, it } from "vitest";
import type { CatalogDraft } from "./api";
import { changesAnything, mayConfirm, nextStep, split } from "./draft";

const draft = (changes: CatalogDraft["changes"], same = false): CatalogDraft => ({
  inForce: "aaaa",
  draft: same ? "aaaa" : "bbbb",
  changes,
  breaking: changes.filter((c) => c.kind === "breaking").length,
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
