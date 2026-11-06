import type { AuthoringReview, ProposalState, ReviewedProposal } from "./api";

/** What a person reads a declaration's kind as; a kind this page does not know is shown under its own name. */
export const kindLabel = (kind: string): string =>
  ({
    entityType: "Kind of thing",
    relation: "Link",
    identity: "Same-thing rule",
    linkage: "Matching rule",
    grouping: "Group",
  })[kind] ?? kind;

/** What a person reads a proposal's place in the draft as. */
export const stateLabel = (state: ProposalState): string => ({ taken: "included", edited: "edited", left: "left out" })[state];

/**
 * One sentence saying what a declaration claims, from its own fields — for a person who reads forms, not catalog files.
 * The declaration itself stays one click away.
 */
export function describe(declaration: Record<string, unknown>): string {
  const text = (key: string) => (typeof declaration[key] === "string" ? (declaration[key] as string) : undefined);
  const fields =
    declaration.fields !== null && typeof declaration.fields === "object" && !Array.isArray(declaration.fields)
      ? Object.keys(declaration.fields as Record<string, unknown>)
      : [];
  switch (declaration.kind) {
    case "entityType": {
      const where = [text("system"), text("table")].filter(Boolean).join(".");
      const key = text("key");
      return (
        `Each row of ${where || "a source"} is one “${text("name")}”` +
        (key ? `, identified by ${key}` : "") +
        (fields.length > 0 ? `. It has the fields ${fields.join(", ")}.` : ".")
      );
    }
    case "relation":
      return `“${text("from")}” points to “${text("to")}” through its ${text("via") ?? "link"} field.`;
    default:
      return `Declares a ${kindLabel(String(declaration.kind))} called “${text("name") ?? ""}”.`;
  }
}

/** How many proposals the draft holds as proposed, changed, and not at all. */
export const tally = (review: AuthoringReview) => ({
  taken: review.proposals.filter((p) => p.state === "taken").length,
  edited: review.proposals.filter((p) => p.state === "edited").length,
  left: review.proposals.filter((p) => p.state === "left").length,
});

/** What a button on a proposal does: take one the draft lacks, leave one it holds. An edited one is already taken, and only leaving it is a choice. */
export const action = (proposal: ReviewedProposal): "take" | "leave" => (proposal.state === "left" ? "take" : "leave");

/** Leaving an edited proposal throws the person's edit away with it, so the screen says so first. */
export const discardsEdit = (proposal: ReviewedProposal) => proposal.state === "edited" && action(proposal) === "leave";

/** The review's request for one proposal. */
export const decisionFor = (proposal: ReviewedProposal) => (action(proposal) === "take" ? { take: [proposal.id] } : { leave: [proposal.id] });

/** The request that takes every proposal the draft lacks, in the order the pass made them. */
export const takeAll = (review: AuthoringReview) => ({ take: review.proposals.filter((p) => p.state === "left").map((p) => p.id) });

/** Whether a refusal of a review asks the builder to read the draft again: it moved. */
export const readAgain = (code: string | undefined) => code === "stale-draft";

/** The text an edit starts from: the declaration the draft holds, or the proposed one when the draft holds none. */
export const editableText = (proposal: ReviewedProposal) => JSON.stringify(proposal.current ?? proposal.declaration, null, 2);

/** What the builder wrote read as one declaration, or why it cannot be sent: the console checks the rest (the kind and name stay, the catalog stays consistent). */
export const parseEdit = (text: string): { ok: true; declaration: Record<string, unknown> } | { ok: false; problem: string } => {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch (e) {
    return { ok: false, problem: `That is not valid JSON: ${e instanceof Error ? e.message : String(e)}` };
  }
  if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) {
    return { ok: false, problem: "A declaration is one JSON object." };
  }
  return { ok: true, declaration: parsed as Record<string, unknown> };
};

/** The review's request for an edit of one proposal. */
export const editFor = (proposal: ReviewedProposal, declaration: Record<string, unknown>) => ({ edit: { [proposal.id]: declaration } });

/** Whether the text still reads as the declaration it started from, so saving would change nothing. */
export const unchanged = (proposal: ReviewedProposal, text: string) => {
  const read = parseEdit(text);
  return read.ok && JSON.stringify(read.declaration) === JSON.stringify(proposal.current ?? proposal.declaration);
};

/** Whether the draft the review names is already the catalog in force: the review is finished and nothing waits to be confirmed. */
export const alreadyInForce = (review: AuthoringReview) => review.draft === review.inForce;
