import type { CatalogDraft, ConfirmedSettlement } from "./api";

/** The changes of a draft in sentences of two kinds: what can break a call that worked, and what only adds vocabulary. */
export const split = (draft: CatalogDraft) => ({
  breaking: draft.changes.filter((c) => c.kind === "breaking"),
  additive: draft.changes.filter((c) => c.kind === "additive"),
});

/** Whether the draft changes the catalog at all: the same declarations are the same version. */
export const changesAnything = (draft: CatalogDraft) => draft.draft !== draft.inForce;

/** Whether the confirm button may be pressed: a draft that changes something, its breaking changes acknowledged. */
export const mayConfirm = (draft: CatalogDraft, acknowledged: boolean) => changesAnything(draft) && (draft.breaking === 0 || acknowledged);

/** What the console says of the reviewed pass after a confirmation; nothing when the console does not record reviews. */
export function settlementLine(settlement: ConfirmedSettlement | null): string | null {
  if (settlement === null) return null;
  return settlement.recorded
    ? `Settled ${settlement.proposals} proposals of ${settlement.pass}: ${settlement.taken} taken, ${settlement.proposals - settlement.taken} not.`
    : `${settlement.pass} was settled by an earlier confirmation.`;
}

/** What a builder does next once the console refuses a confirmation with this code; every refusal but `breaking` asks for a fresh read. */
export function nextStep(code: string | undefined): "read-again" | "acknowledge" | "none" {
  switch (code) {
    case "stale-base":
    case "stale-draft":
    case "nothing-to-confirm":
      return "read-again";
    case "breaking":
      return "acknowledge";
    default:
      return "none";
  }
}
