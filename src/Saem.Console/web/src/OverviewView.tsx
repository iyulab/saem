import { useEffect, useState } from "react";
import { api, whyNot, type Answer, type Overview } from "./api";
import { declaredSentence } from "./overview";

/** What is waiting at a glance: the catalog in force and the last check of it against the sources. */
export function OverviewView({ onOpenCheck }: { onOpenCheck?: () => void }) {
  const [overview, setOverview] = useState<Answer<Overview> | null>(null);

  useEffect(() => {
    api.overview().then(setOverview);
  }, []);

  if (overview === null) {
    return <p>Loading…</p>;
  }
  if (!overview.ok) {
    return <p role="alert">{whyNot(overview, "The overview could not be read.")}</p>;
  }

  const { catalogVersion, declarations, check } = overview.value;
  return (
    <section>
      <h1>Overview</h1>
      <div className="cards">
        <article className="card">
          <h2>What is declared</h2>
          <p className="big">{declaredSentence(declarations)}</p>
          <p className="muted">
            Version <code>{catalogVersion}</code>
          </p>
        </article>
        <article className={check && (!check.holds || !check.current) ? "card attention" : "card"}>
          <h2>Checked against the sources?</h2>
          {check === null && (
            <>
              <p className="big">Not checked yet</p>
              <details>
                <summary>How a check is run</summary>
                <p className="muted">
                  A builder runs <code>saem-materialize check --report</code> with their own account; the console then shows its findings here.
                </p>
              </details>
            </>
          )}
          {check !== null && (
            <>
              <p>
                {check.holds ? "Every declaration held" : check.violations === 1 ? "1 finding does not hold" : `${check.violations} findings do not hold`}
                {check.checkedAt && <span className="muted"> · {new Date(check.checkedAt).toLocaleString()}</span>}
              </p>
              {!check.current && (
                <p className="refusal">
                  It checked catalog <code>{check.catalogVersion}</code>, not the one in force — it says nothing about the declarations now.
                </p>
              )}
              {onOpenCheck && (
                <button className="link" onClick={onOpenCheck}>
                  Read the findings
                </button>
              )}
            </>
          )}
        </article>
      </div>
    </section>
  );
}
