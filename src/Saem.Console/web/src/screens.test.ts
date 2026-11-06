import { describe, expect, it } from "vitest";
import { screens, tabsFor } from "./screens";

describe("the screens a person is shown", () => {
  it("puts the four screens of the way from proposals to a catalog in the bar and the rest behind More", () => {
    const { main, more } = tabsFor(["builder"]);

    expect(main.map((s) => s.title)).toEqual(["Overview", "Catalog", "Review", "Draft"]);
    expect(more.map((s) => s.title)).toEqual(["Knowledge graph", "Local graph", "Check", "Groups", "Judgments"]);
  });

  it("shows a reader only the screens their role opens, wherever they sit", () => {
    const { main, more } = tabsFor(["reader"]);

    expect(main.map((s) => s.title)).toEqual(["Overview", "Catalog"]);
    expect(more.map((s) => s.title)).toEqual(["Knowledge graph"]);
  });

  it("lists every screen exactly once", () => {
    expect(new Set(screens.map((s) => s.screen)).size).toBe(screens.length);
  });
});
