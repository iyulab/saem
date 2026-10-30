import { describe, expect, it } from "vitest";
import type { AuthoringReview, ProposalState, ReviewedProposal } from "./api";
import { action, decisionFor, discardsEdit, readAgain, takeAll, tally } from "./review";

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
});
