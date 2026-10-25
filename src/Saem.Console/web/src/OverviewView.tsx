import { useEffect, useState } from "react";
import { api, type Answer, type Overview } from "./api";
import { kinds } from "./catalog";

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
    return <p role="alert">{"problem" in overview ? overview.problem : "The overview could not be read."}</p>;
  }

  const { catalogVersion, declarations, check } = overview.value;
  return (
    <section>
      <h1>Overview</h1>
      <div className="cards">
        <article className="card">
          <h2>Catalog in force</h2>
          <p>
            <code>{catalogVersion}</code>
          </p>
          <ul className="counts">
            {kinds.map((k) => (
              <li key={k.kind}>
                <strong>{declarations[k.kind] ?? 0}</strong> {k.title.toLowerCase()}
              </li>
            ))}
          </ul>
        </article>
        <article className={check && (!check.holds || !check.current) ? "card attention" : "card"}>
          <h2>Last check against the sources</h2>
          {check === null && <p className="muted">No check report. A builder runs <code>saem-materialize check --report</code> with their own account; the console shows its findings.</p>}
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
