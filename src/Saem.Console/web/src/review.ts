import type { AuthoringReview, ReviewedProposal } from "./api";

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
