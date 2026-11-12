import { useCallback, useEffect, useState } from "react";
import { api, whyNot, type Answer, type AuthoringReview, type ReviewedProposal } from "./api";
import { action, alreadyInForce, decisionFor, describe, discardsEdit, editFor, editableText, fieldRows, kindLabel, parseEdit, readAgain, rowsProblem, stateLabel, takeAll, tally, unchanged, withFields, type FieldRow } from "./review";

/**
 * The declarations an authoring pass proposed, one by one against the draft: take one the draft lacks, leave one it holds.
 * The draft file is the whole state of the review — what is taken here is what the Draft screen then confirms.
 */
export function ReviewView({ onOpenDraft, onOpenCatalog }: { onOpenDraft: () => void; onOpenCatalog?: () => void }) {
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
    const problem = whyNot(review, "The proposals could not be read.");
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
    setRefusal({ problem: whyNot(answer, "The review was refused."), code: "code" in answer ? answer.code : undefined });
    return false;
  };

  const taking = takeAll(seen).take;
  // After a confirmation the draft is the catalog in force: the review reads as finished, not as a first step to start again.
  const finished = alreadyInForce(seen);
  return (
    <section>
      <p className="step">{finished ? "Reviewed · The draft is in force" : "Step 1 of 2 · Review what was proposed"}</p>
      <h1>Review</h1>
      {finished ? (
        <p className="lead">
          This review is finished: the draft you confirmed is in force, and questions are answered with it. You can still change your mind — leave out or edit a proposal here,
          and the change waits for you to confirm it in the next step.
        </p>
      ) : (
        <p className="lead">
          An authoring pass read your forms and proposed how to describe them. Every proposal starts as <strong>included</strong>: it goes into the draft and takes effect
          only when you confirm it in the next step. Leave out what is wrong, edit what is nearly right, then continue.
        </p>
      )}
      <p className="tally">
        <strong>{counts.taken}</strong> included · <strong>{counts.edited}</strong> edited · <strong>{counts.left}</strong> left out
      </p>
      <table>
        <tbody>
          {seen.proposals.map((p) => (
            <ProposalRow key={p.id} proposal={p} busy={working} onDecide={() => decide(decisionFor(p))} onEdit={(declaration) => decide(editFor(p, declaration))} />
          ))}
        </tbody>
      </table>
      <p className="actions">
        {finished && onOpenCatalog ? (
          <button className="button" onClick={onOpenCatalog}>
            See what is now declared
          </button>
        ) : (
          <button className="button" onClick={onOpenDraft}>
            Continue: check and confirm
          </button>
        )}{" "}
        {taking.length > 0 && (
          <>
            <button disabled={working} onClick={() => decide({ take: taking })}>
              Include everything again ({taking.length})
            </button>{" "}
          </>
        )}
        <button className="link" onClick={read}>
          Read again
        </button>
      </p>
      <details className="tech">
        <summary>Technical details</summary>
        <p className="muted">
          Pass <code>{seen.pass}</code> · catalog in force <code>{seen.inForce}</code> · draft <code>{seen.draft}</code>
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
      </details>
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
  const [rows, setRows] = useState<FieldRow[] | null>(null);
  const [unreadable, setUnreadable] = useState<string | null>(null);
  const base = proposal.current ?? proposal.declaration;
  // A kind of thing is edited as a choice of fields; anything else, and anyone who wants every detail, edits the declaration as text.
  const startEdit = () => {
    const offered = fieldRows(base);
    if (offered) {
      setRows(offered);
    } else {
      setText(editableText(proposal));
    }
  };
  const saveRows = async () => {
    if (rows && (await onEdit(withFields(base, rows)))) {
      setRows(null);
    }
  };
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
        <span className="kind">{kindLabel(proposal.kind)}</span>
        <br />
        <code>{proposal.name}</code>
      </th>
      <td className="says">{describe(proposal.current ?? proposal.declaration)}</td>
      <td>
        <span className={`state ${proposal.state}`}>{stateLabel(proposal.state)}</span>
        {discardsEdit(proposal) && <span className="muted"> · leaving it out discards your edit</span>}
        <details>
          <summary>{proposal.state === "edited" ? "Show the proposal and your edit" : "Show the declaration"}</summary>
          <pre>{JSON.stringify(proposal.declaration, null, 2)}</pre>
          {proposal.state === "edited" && proposal.current ? <pre>{JSON.stringify(proposal.current, null, 2)}</pre> : null}
        </details>
      </td>
      <td>
        <button disabled={busy} onClick={onDecide}>
          {leaving ? "Leave out" : "Include"}
        </button>{" "}
        <button className="link" disabled={busy || text !== null || rows !== null} onClick={startEdit}>
          Edit
        </button>
      </td>
    </tr>
    {rows !== null && (
      <tr>
        <td colSpan={4}>
          <p>
            Choose which fields of <code>{proposal.name}</code> people can ask for. A field you uncheck is taken out of the declaration; where the values come from stays as the pass read it.
          </p>
          <table>
            <tbody>
              {rows.map((r) => (
                <tr key={r.name}>
                  <td>
                    <label>
                      <input
                        type="checkbox"
                        checked={r.keep}
                        aria-label={`Keep ${r.name}`}
                        onChange={(e) => setRows(rows.map((x) => (x.name === r.name ? { ...x, keep: e.target.checked } : x)))}
                      />{" "}
                      {r.name}
                    </label>
                  </td>
                  <td className="muted">read from <code>{r.column}</code></td>
                </tr>
              ))}
            </tbody>
          </table>
          {rowsProblem(rows) && <p className="muted">{rowsProblem(rows)}</p>}
          <button disabled={busy || rowsProblem(rows) !== null} onClick={saveRows}>
            Save the edit
          </button>{" "}
          <button className="link" disabled={busy} onClick={() => { setText(JSON.stringify(withFields(base, rows), null, 2)); setRows(null); }}>
            Edit the declaration as text instead
          </button>{" "}
          <button className="link" disabled={busy} onClick={() => setRows(null)}>
            Cancel
          </button>
        </td>
      </tr>
    )}
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
