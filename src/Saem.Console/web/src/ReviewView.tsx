import { useCallback, useEffect, useState } from "react";
import { api, type Answer, type AuthoringReview, type ReviewedProposal } from "./api";
import { action, decisionFor, discardsEdit, readAgain, takeAll, tally } from "./review";

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
  const decide = async (decision: { take?: string[]; leave?: string[] }) => {
    setWorking(true);
    const answer = await api.decide(seen.draft, decision);
    setWorking(false);
    if (answer.ok) {
      setRefusal(null);
      setReview(answer);
      return;
    }
    setRefusal({ problem: "problem" in answer ? answer.problem : "The review was refused.", code: "code" in answer ? answer.code : undefined });
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
            <ProposalRow key={p.id} proposal={p} busy={working} onDecide={() => decide(decisionFor(p))} />
          ))}
        </tbody>
      </table>
      <p>
        {taking.length > 0 && (
          <>
            <button disabled={working} onClick={() => decide({ take: taking })}>
              Take all {taking.length} left
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

function ProposalRow({ proposal, busy, onDecide }: { proposal: ReviewedProposal; busy: boolean; onDecide: () => void }) {
  const leaving = action(proposal) === "leave";
  return (
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
        </button>
      </td>
    </tr>
  );
}
