export type Screen = "overview" | "catalog" | "review" | "draft" | "graph" | "local" | "check" | "groups" | "judgments";

/**
 * The console's screens. The four a person uses to get from proposals to a declared catalog sit in the bar;
 * the rest (`more`) wait behind one "More" so a first-time reader is not shown nine tabs for a job of four.
 */
export const screens: ReadonlyArray<{ screen: Screen; title: string; builders?: boolean; more?: boolean }> = [
  { screen: "overview", title: "Overview" },
  { screen: "catalog", title: "Catalog" },
  { screen: "review", title: "Review", builders: true },
  { screen: "draft", title: "Draft", builders: true },
  { screen: "graph", title: "Knowledge graph", more: true },
  { screen: "local", title: "Local graph", builders: true, more: true },
  { screen: "check", title: "Check", builders: true, more: true },
  { screen: "groups", title: "Groups", builders: true, more: true },
  { screen: "judgments", title: "Judgments", builders: true, more: true },
];

/** The screens a person's roles open, split into the ones in the bar and the ones behind "More". */
export function tabsFor(roles: readonly string[]) {
  const open = screens.filter((s) => !s.builders || roles.includes("builder"));
  return { main: open.filter((s) => !s.more), more: open.filter((s) => s.more) };
}
