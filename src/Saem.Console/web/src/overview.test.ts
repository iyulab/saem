import { describe, expect, it } from "vitest";
import { declaredSentence } from "./overview";

describe("saying what a catalog declares", () => {
  it("counts the kinds a catalog holds in words", () => {
    expect(declaredSentence({ entityType: 2, relation: 1 })).toBe("2 kinds of things and 1 link");
    expect(declaredSentence({ entityType: 1 })).toBe("1 kind of thing");
    expect(declaredSentence({ entityType: 3, relation: 2, identity: 1 })).toBe("3 kinds of things, 2 links and 1 same-thing rule");
  });

  it("says so when nothing is declared", () => {
    expect(declaredSentence({})).toBe("Nothing is declared yet");
    expect(declaredSentence({ entityType: 0 })).toBe("Nothing is declared yet");
  });
});
