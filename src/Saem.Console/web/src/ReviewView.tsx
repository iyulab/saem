import { useCallback, useEffect, useState } from "react";
import { api, type Answer, type AuthoringReview, type ReviewedProposal } from "./api";
import { action, decisionFor, discardsEdit, editFor, editableText, parseEdit, readAgain, takeAll, tally, unchanged } from "./review";

/**
 * The declarations an authoring pass proposed, one by one against the draft: take one the draft lacks, leave one it holds.
 * The draft file is the whole state of the review — what is taken here is what the Draft screen then confirms.
 */
export function ReviewView({ onOpenDraft }: { onOpenDraft: () => void }) {
  const [review, setReview] = useState<Answer<AuthoringReview> | null>(null);
  const [working, setWorking] = useState(false);
  const [refusal, setRefusal] = useState<{ problem: string; code?: string } | null>(null);

  const read = useCallback(() => {
    setRefusal(null);
    api.review().then(setReview);
  }, []);

  useEffect(read, [read]);

  if (review === null) {
    return <p>Reading the proposals…</p>;
  }
  if (!review.ok) {
    const problem = "problem" in review ? review.problem : "The proposals could not be read.";
    return (
      <section>
        <h1>Review</h1>
        <p role="alert">{problem}</p>
        <button onClick={read}>Read again</button>
      </section>
    );
  }

  const seen = review.value;
  const counts = tally(seen);
  const decide = async (decision: Parameters<typeof api.decide>[1]): Promise<boolean> => {
    setWorking(true);
    const answer = await api.decide(seen.draft, decision);
    setWorking(false);
    if (answer.ok) {
      setRefusal(null);
      setReview(answer);
      return true;
    }
    setRefusal({ problem: "problem" in answer ? answer.problem : "The review was refused.", code: "code" in answer ? answer.code : undefined });
    return false;
  };

  const taking = takeAll(seen).take;
  return (
    <section>
      <h1>Review</h1>
      <p className="muted">
        Pass <code>{seen.pass}</code> · in force <code>{seen.inForce}</code> · draft <code>{seen.draft}</code>
      </p>
      {seen.generation && (
        <p className="muted">
          Made with {seen.generation.model} · proposer {seen.generation.proposerVersion} · prompt <code>{seen.generation.promptFingerprint}</code>
          {seen.generation.knownTypesPromptFingerprint ? (
            <>
              {" + "}
              <code>{seen.generation.knownTypesPromptFingerprint}</code>
            </>
          ) : null}
          {seen.generation.modelCallsRetried > 0 ? ` · ${seen.generation.modelCallsRetried} model call(s) retried` : null}
        </p>
      )}
      <p>
        {counts.taken} taken · {counts.edited} edited · {counts.left} left
      </p>
      <table>
        <tbody>
          {seen.proposals.map((p) => (
            <ProposalRow key={p.id} proposal={p} busy={working} onDecide={() => decide(decisionFor(p))} onEdit={(declaration) => decide(editFor(p, declaration))} />
          ))}
        </tbody>
      </table>
      <p>
        {taking.length > 0 && (
          <>
            <button disabled={working} onClick={() => decide({ take: taking })}>
              Take back all left ({taking.length})
            </button>{" "}
          </>
        )}
        <button className="link" onClick={read}>
          Read again
        </button>{" "}
        <button className="link" onClick={onOpenDraft}>
          Go to the draft to confirm
        </button>
      </p>
      {refusal && (
        <div role="alert">
          <p>{refusal.problem}</p>
          {readAgain(refusal.code) && <button onClick={read}>Read the draft as it is now</button>}
        </div>
      )}
    </section>
  );
}

function ProposalRow({
  proposal,
  busy,
  onDecide,
  onEdit,
}: {
  proposal: ReviewedProposal;
  busy: boolean;
  onDecide: () => void;
  onEdit: (declaration: Record<string, unknown>) => Promise<boolean>;
}) {
  const leaving = action(proposal) === "leave";
  const [text, setText] = useState<string | null>(null);
  const [unreadable, setUnreadable] = useState<string | null>(null);
  const save = async () => {
    const read = parseEdit(text ?? "");
    if (!read.ok) {
      setUnreadable(read.problem);
      return;
    }
    setUnreadable(null);
    if (await onEdit(read.declaration)) {
      setText(null);
    }
  };
  return (
    <>
    <tr>
      <th scope="row">
        {proposal.kind} <code>{proposal.name}</code>
      </th>
      <td>
        <span className={`state ${proposal.state}`}>{proposal.state}</span>
        {discardsEdit(proposal) && <span className="muted"> · leaving it discards your edit</span>}
      </td>
      <td>
        <details>
          <summary>{proposal.state === "edited" ? "As proposed · as the draft holds it" : "Declaration"}</summary>
          <pre>{JSON.stringify(proposal.declaration, null, 2)}</pre>
          {proposal.state === "edited" && proposal.current ? <pre>{JSON.stringify(proposal.current, null, 2)}</pre> : null}
        </details>
      </td>
      <td>
        <button disabled={busy} onClick={onDecide}>
          {leaving ? "Leave" : "Take"}
        </button>{" "}
        <button className="link" disabled={busy || text !== null} onClick={() => setText(editableText(proposal))}>
          Edit
        </button>
      </td>
    </tr>
    {text !== null && (
      <tr>
        <td colSpan={4}>
          <label>
            The declaration the draft will hold for <code>{proposal.id}</code> — its kind and name stay; the console checks the catalog is still consistent.
            <textarea aria-label={`Declaration of ${proposal.id}`} rows={12} value={text} onChange={(e) => { setText(e.target.value); setUnreadable(null); }} spellCheck={false} />
          </label>
          {unreadable && <p role="alert">{unreadable}</p>}
          <button disabled={busy || unchanged(proposal, text)} onClick={save}>
            Save the edit
          </button>{" "}
          <button className="link" disabled={busy} onClick={() => { setText(null); setUnreadable(null); }}>
            Cancel
          </button>
        </td>
      </tr>
    )}
    </>
  );
}
