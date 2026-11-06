import { useCallback, useEffect, useState } from "react";
import { api, type Answer, type CatalogDraft, type Confirmed } from "./api";
import { mayConfirm, nextStep, settlementLine, split } from "./draft";
import { kindLabel } from "./review";

/** The draft against the catalog in force: what it adds, what can break, and the confirmation that puts it in force. */
export function DraftView() {
  const [draft, setDraft] = useState<Answer<CatalogDraft> | null>(null);
  const [acknowledged, setAcknowledged] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [refusal, setRefusal] = useState<{ problem: string; code?: string } | null>(null);
  const [done, setDone] = useState<Confirmed | null>(null);

  const read = useCallback(() => {
    setAcknowledged(false);
    setRefusal(null);
    api.draft().then(setDraft);
  }, []);

  useEffect(read, [read]);

  if (draft === null) {
    return <p>Reading the draft…</p>;
  }
  if (!draft.ok) {
    const problem = "problem" in draft ? draft.problem : "The draft could not be read.";
    return (
      <section>
        <h1>Draft</h1>
        <p role="alert">{problem}</p>
        <button onClick={read}>Read again</button>
      </section>
    );
  }

  const { breaking, additive } = split(draft.value);
  const confirm = async () => {
    setConfirming(true);
    const answer = await api.confirm(draft.value, acknowledged);
    setConfirming(false);
    if (answer.ok) {
      setDone(answer.value);
      setDraft(null);
      api.draft().then(setDraft);
      return;
    }
    setRefusal({ problem: "problem" in answer ? answer.problem : "The confirmation was refused.", code: "code" in answer ? answer.code : undefined });
  };

  return (
    <section>
      <p className="step">Step 2 of 2 · Confirm the draft</p>
      <h1>Draft</h1>
      <p className="lead">
        This is what changes if you confirm: the catalog in force is replaced by the draft, and questions are answered with it from then on. Nothing changes until you
        press <strong>Confirm the draft</strong>. To change a proposal, go back to the review.
      </p>
      {done && (
        <p role="status" className="done">
          <strong>Confirmed.</strong> The draft is now in force: questions are answered with it from now on. {settlementLine(done.settlement)}{" "}
          <span className="muted">
            (Version <code>{done.to}</code>, was <code>{done.from}</code>.)
          </span>
        </p>
      )}
      {draft.value.draft === draft.value.inForce ? (
        <p>The draft declares what the catalog in force already declares.</p>
      ) : (
        <>
          <Changes title="Can break what was built on the catalog" changes={breaking} strong />
          <Changes title="Only adds to what is declared" changes={additive} />
          {draft.value.breaking > 0 && (
            <label className="block">
              <input type="checkbox" checked={acknowledged} onChange={(e) => setAcknowledged(e.target.checked)} /> I have read the {draft.value.breaking} breaking
              change(s) and want them in force.
            </label>
          )}
          <p>
            <button className="button" disabled={!mayConfirm(draft.value, acknowledged) || confirming} onClick={confirm}>
              Confirm the draft
            </button>{" "}
            <button className="link" onClick={read}>
              Read again
            </button>
          </p>
        </>
      )}
      {refusal && (
        <div role="alert">
          <p>{refusal.problem}</p>
          {nextStep(refusal.code) === "read-again" && (
            <button onClick={read}>Read the draft against the catalog now in force</button>
          )}
        </div>
      )}
      <details className="tech">
        <summary>Technical details</summary>
        <p className="muted">
          In force <code>{draft.value.inForce}</code> · draft <code>{draft.value.draft}</code>
        </p>
      </details>
    </section>
  );
}

function Changes({ title, changes, strong }: { title: string; changes: Array<{ declaration: string; change: string }>; strong?: boolean }) {
  if (changes.length === 0) {
    return null;
  }
  return (
    <details open>
      <summary>
        {strong ? <strong>{title}</strong> : title} <span className="muted">({changes.length})</span>
      </summary>
      <table>
        <tbody>
          {changes.map((c) => (
            <tr key={`${c.declaration} ${c.change}`}>
              <th scope="row">{plainDeclaration(c.declaration)}</th>
              <td>{c.change}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </details>
  );
}

/** "entityType items" as a person reads it: the kind in words, then the name. A name that does not split is shown as it is. */
function plainDeclaration(declaration: string): string {
  const at = declaration.indexOf(" ");
  return at < 0 ? declaration : `${kindLabel(declaration.slice(0, at))} · ${declaration.slice(at + 1)}`;
}
