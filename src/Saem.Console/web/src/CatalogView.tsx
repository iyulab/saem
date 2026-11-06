import { useEffect, useState } from "react";
import { api, type Answer, type Catalog } from "./api";
import { askExample, byKind } from "./catalog";
import { describe } from "./review";

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
  const example = askExample(catalog.value.declarations);
  return (
    <section>
      <h1>Catalog</h1>
      <p className="muted">
        Version <code>{catalog.value.version}</code> · {catalog.value.declarations.length} declarations
      </p>
      {sections.length === 0 && <p>The catalog declares nothing yet.</p>}
      {example && (
        <details className="ask">
          <summary>How to ask this catalog a question</summary>
          <p>
            People and AI assistants ask through the saem MCP server&rsquo;s <code>lookup</code> tool, signed in as themselves, so each gets only what their own access
            allows. A question names where to start (a kind of thing and the key of one row), the links to follow, and the fields to read:
          </p>
          <pre>{JSON.stringify(example, null, 2)}</pre>
          <p className="muted">
            Every answer comes with the source records it was read from. A question that names something not declared here is refused with an error saying what is missing.
            Ask whoever runs the MCP server for its address.
          </p>
        </details>
      )}
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
                  <td>
                    <span className="says">{describe(d)}</span>
                    <details>
                      <summary>Show the declaration</summary>
                      <pre>{JSON.stringify(d, null, 2)}</pre>
                    </details>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </details>
      ))}
    </section>
  );
}
