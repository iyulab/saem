import { describe, expect, it } from "vitest";
import type { AuthoringReview, ProposalState, ReviewedProposal } from "./api";
import { action, alreadyInForce, decisionFor, fieldRows, rowsProblem, withFields, describe as describeDeclaration, discardsEdit, editFor, editableText, kindLabel, parseEdit, readAgain, stateLabel, takeAll, tally, unchanged } from "./review";

const proposal = (name: string, state: ProposalState): ReviewedProposal => ({
  id: `entityType:${name}`,
  kind: "entityType",
  name,
  state,
  declaration: { kind: "entityType", name },
});

const review = (...proposals: ReviewedProposal[]): AuthoringReview => ({ pass: "pass-1", inForce: "aaaa", draft: "bbbb", proposals });

describe("reviewing a pass's proposals against the draft", () => {
  it("counts what the draft holds as proposed, changed, and not at all", () => {
    expect(tally(review(proposal("A", "taken"), proposal("B", "taken"), proposal("C", "edited"), proposal("D", "left")))).toEqual({ taken: 2, edited: 1, left: 1 });
  });

  it("offers to take what the draft lacks and to leave what it holds", () => {
    expect(action(proposal("A", "left"))).toBe("take");
    expect(action(proposal("A", "taken"))).toBe("leave");
    expect(action(proposal("A", "edited"))).toBe("leave");
  });

  it("warns only when leaving would throw away an edit", () => {
    expect(discardsEdit(proposal("A", "edited"))).toBe(true);
    expect(discardsEdit(proposal("A", "taken"))).toBe(false);
    expect(discardsEdit(proposal("A", "left"))).toBe(false);
  });

  it("asks the console for the one proposal's id", () => {
    expect(decisionFor(proposal("A", "left"))).toEqual({ take: ["entityType:A"] });
    expect(decisionFor(proposal("B", "taken"))).toEqual({ leave: ["entityType:B"] });
  });

  it("takes every proposal the draft lacks, in the pass's order", () => {
    expect(takeAll(review(proposal("A", "left"), proposal("B", "taken"), proposal("C", "left")))).toEqual({ take: ["entityType:A", "entityType:C"] });
  });

  it("reads the draft again only when it moved", () => {
    expect(readAgain("stale-draft")).toBe(true);
    expect(readAgain("inconsistent")).toBe(false);
    expect(readAgain(undefined)).toBe(false);
  });

  it("starts an edit from what the draft holds, or from the proposal when the draft holds none", () => {
    const held = { ...proposal("A", "edited"), current: { kind: "entityType", name: "A", table: "changed" } };
    expect(JSON.parse(editableText(held))).toEqual({ kind: "entityType", name: "A", table: "changed" });
    expect(JSON.parse(editableText(proposal("B", "left")))).toEqual({ kind: "entityType", name: "B" });
  });

  it("reads what the builder wrote as one declaration or says why not", () => {
    expect(parseEdit('{ "kind": "entityType", "name": "A" }')).toEqual({ ok: true, declaration: { kind: "entityType", name: "A" } });
    const bad = parseEdit("{ nope");
    expect(bad.ok).toBe(false);
    expect(parseEdit("[1]")).toEqual({ ok: false, problem: "A declaration is one JSON object." });
    expect(parseEdit("null")).toEqual({ ok: false, problem: "A declaration is one JSON object." });
  });

  it("asks the console to hold the written declaration in the proposal's place", () => {
    expect(editFor(proposal("A", "taken"), { kind: "entityType", name: "A", table: "t" })).toEqual({ edit: { "entityType:A": { kind: "entityType", name: "A", table: "t" } } });
  });

  it("sees when saving would change nothing", () => {
    const p = proposal("A", "taken");
    expect(unchanged(p, editableText(p))).toBe(true);
    expect(unchanged(p, '{ "kind": "entityType", "name": "A", "table": "t" }')).toBe(false);
    expect(unchanged(p, "{ nope")).toBe(false);
  });
});

describe("saying a proposal in words a person who reads forms understands", () => {
  it("says what a type stands on, how it is identified and what it holds", () => {
    expect(
      describeDeclaration({ kind: "entityType", name: "items", system: "forms", table: "items", key: "item_code", fields: { item_name: "item_name", owner: "owner" } }),
    ).toBe("Each row of forms.items is one “items”, identified by item_code. It has the fields item_name, owner.");
  });

  it("says where a relation leads", () => {
    expect(describeDeclaration({ kind: "relation", name: "item", from: "work_orders", via: "item_code", to: "items" })).toBe(
      "“work_orders” points to “items” through its item_code field.",
    );
  });

  it("does not fail on a type with nothing but a name, nor on a kind it does not know", () => {
    expect(describeDeclaration({ kind: "entityType", name: "A" })).toBe("Each row of a source is one “A”.");
    expect(describeDeclaration({ kind: "grouping", name: "g" })).toBe("Declares a Group called “g”.");
    expect(describeDeclaration({ kind: "mystery", name: "m" })).toBe("Declares a mystery called “m”.");
  });

  it("names kinds and states plainly", () => {
    expect([kindLabel("entityType"), kindLabel("relation"), kindLabel("mystery")]).toEqual(["Kind of thing", "Link", "mystery"]);
    expect([stateLabel("taken"), stateLabel("edited"), stateLabel("left")]).toEqual(["included", "edited", "left out"]);
  });
});

describe("a review whose draft is already in force", () => {
  it("reads as finished only while the draft names the catalog in force", () => {
    const seen = review(proposal("items", "taken"));

    expect(alreadyInForce({ ...seen, draft: "v2", inForce: "v2" })).toBe(true);
    expect(alreadyInForce({ ...seen, draft: "v3", inForce: "v2" })).toBe(false);
  });
});

describe("editing which fields a kind of thing keeps", () => {
  const items = {
    kind: "entityType",
    name: "items",
    system: "forms",
    table: "items",
    key: "item_code",
    fields: { item_name: "item_name", owner: "owner_col" },
    kinds: { item_name: "text", owner: "text" },
  };

  it("offers a row per field with the column it is read from, and nothing for other kinds", () => {
    expect(fieldRows(items)).toEqual([
      { name: "item_name", column: "item_name", keep: true },
      { name: "owner", column: "owner_col", keep: true },
    ]);
    expect(fieldRows({ kind: "relation", name: "r", from: "a", to: "b" })).toBeNull();
    expect(fieldRows({ ...items, fields: {} })).toBeNull();
  });

  it("drops an unchecked field and the kind declared for it, and changes nothing else", () => {
    const rows = fieldRows(items)!.map((r) => (r.name === "owner" ? { ...r, keep: false } : r));

    expect(withFields(items, rows)).toEqual({ ...items, fields: { item_name: "item_name" }, kinds: { item_name: "text" } });
  });

  it("leaves a declaration without kinds without kinds", () => {
    const { kinds: _kinds, ...bare } = items;
    const rows = fieldRows(bare)!.map((r) => (r.name === "owner" ? { ...r, keep: false } : r));

    expect(withFields(bare, rows)).toEqual({ ...bare, fields: { item_name: "item_name" } });
  });

  it("cannot be saved while nothing is dropped or while nothing is kept", () => {
    const rows = fieldRows(items)!;

    expect(rowsProblem(rows)).toBe("Nothing is changed yet.");
    expect(rowsProblem(rows.map((r) => ({ ...r, keep: false })))).toBe("Keep at least one field.");
    expect(rowsProblem([{ ...rows[0], keep: false }, rows[1]])).toBeNull();
  });
});
