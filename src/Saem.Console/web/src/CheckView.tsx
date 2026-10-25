import { useEffect, useState } from "react";
import { api, type Answer, type CheckReport } from "./api";

const subject = (f: { type: string; relation: string | null }) => (f.relation ? `${f.type}.${f.relation}` : f.type);

/**
 * The last check of the declarations against the sources, as the builder who ran it reported it — without the records
 * it cites: those are rows the checking account could read, which whoever looks here may not.
 */
export function CheckView() {
  const [report, setReport] = useState<Answer<CheckReport> | null>(null);
  const [all, setAll] = useState(false);

  useEffect(() => {
    api.check().then(setReport);
  }, []);

  if (report === null) {
    return <p>Loading the check…</p>;
  }
  if (!report.ok) {
    return <p role="alert">{"problem" in report ? report.problem : "Only builders read the check."}</p>;
  }

  const r = report.value;
  const findings = all ? r.findings : r.findings.filter((f) => f.violation);
  return (
    <section>
      <h1>Check against the sources</h1>
      <p className="muted">
        Catalog <code>{r.catalogVersion}</code> · {new Date(r.checkedAt).toLocaleString()} · {r.holds ? "holds" : "does not hold"} · records the check cites stay in its report file
      </p>
      {r.against && (
        <details open>
          <summary>
            Against catalog <code>{r.against.catalogVersion}</code> it replaces — {r.against.worse === 0 ? "nothing worse" : `${r.against.worse} worse`}
          </summary>
          <table>
            <tbody>
              {r.against.differences.map((d) => (
                <tr key={`${subject(d)}:${d.kind}`} className={d.worse ? "worse" : undefined}>
                  <th scope="row">{subject(d)}</th>
                  <td>{d.kind}</td>
                  <td>
                    {d.before ?? "–"} → {d.after ?? "–"}
                  </td>
                  <td>{d.worse ? "worse" : ""}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </details>
      )}
      <label className="muted">
        <input type="checkbox" checked={all} onChange={(e) => setAll(e.target.checked)} /> show the counts that hold, too
      </label>
      {findings.length === 0 && <p>No finding says a declaration does not hold.</p>}
      <table>
        <tbody>
          {findings.map((f) => (
            <tr key={`${subject(f)}:${f.kind}`} className={f.violation ? "worse" : undefined}>
              <th scope="row">{subject(f)}</th>
              <td>{f.kind}</td>
              <td>{f.what}</td>
              <td className="muted">{f.recordsCited > 0 ? `${f.recordsCited} cited in the file` : ""}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
