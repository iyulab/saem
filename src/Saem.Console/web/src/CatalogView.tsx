import { useEffect, useState } from "react";
import { api, type Answer, type Catalog } from "./api";
import { byKind, summary } from "./catalog";

/** The declarations in force, kind by kind, as a catalog file holds them. */
export function CatalogView() {
  const [catalog, setCatalog] = useState<Answer<Catalog> | null>(null);

  useEffect(() => {
    api.catalog().then(setCatalog);
  }, []);

  if (catalog === null) {
    return <p>Loading the catalog…</p>;
  }
  if (!catalog.ok) {
    return <p role="alert">{"problem" in catalog ? catalog.problem : "The catalog could not be read."}</p>;
  }

  const sections = byKind(catalog.value.declarations);
  return (
    <section>
      <h1>Catalog</h1>
      <p className="muted">
        Version <code>{catalog.value.version}</code> · {catalog.value.declarations.length} declarations
      </p>
      {sections.length === 0 && <p>The catalog declares nothing yet.</p>}
      {sections.map((s) => (
        <details key={s.kind} open>
          <summary>
            {s.title} <span className="muted">({s.declarations.length})</span>
          </summary>
          <table>
            <tbody>
              {s.declarations.map((d) => (
                <tr key={d.name}>
                  <th scope="row">{d.name}</th>
                  <td>{summary(d)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </details>
      ))}
    </section>
  );
}
