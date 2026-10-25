import cytoscape from "cytoscape";
import { useEffect, useMemo, useRef, useState } from "react";
import { api, type Answer, type Catalog, type EntityKey } from "./api";
import { noSystem, sizeOf, systemColors } from "./graph";
import { empty, fieldsOf, idOf, maxDepth, refusalText, step, systemOf, walksFrom, type LocalGraph } from "./localGraph";

/**
 * A local graph: one entity and what the person can reach from it, a relation at a time, up to three relations deep.
 * Every step is a lookup asked as the person looking — what they cannot see is said, not hidden — and nothing walked is
 * kept beyond this page.
 */
export function LocalGraphView() {
  const [catalog, setCatalog] = useState<Answer<Catalog> | null>(null);
  const [graph, setGraph] = useState<LocalGraph>(empty);
  const [selected, setSelected] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const [type, setType] = useState("");
  const [key, setKey] = useState("");
  const canvas = useRef<HTMLDivElement>(null);
  const drawn = useRef<cytoscape.Core | null>(null);

  useEffect(() => {
    api.catalog().then((c) => {
      setCatalog(c);
      if (c.ok) {
        setType(c.value.declarations.find((d) => d.kind === "entityType")?.name ?? "");
      }
    });
  }, []);

  const colors = useMemo(
    () => new Map(catalog?.ok ? systemColors(catalog.value.declarations.map((d) => (d.kind === "entityType" ? String(d.system) : null))).map((s) => [s.system, s.color]) : []),
    [catalog],
  );

  useEffect(() => {
    if (!catalog?.ok || !canvas.current || graph.nodes.size === 0) {
      return;
    }
    const ink = getComputedStyle(document.documentElement).getPropertyValue("--fg").trim() || "#1d1f23";
    const cy = cytoscape({
      container: canvas.current,
      elements: [
        ...[...graph.nodes.values()].map((n) => ({
          group: "nodes" as const,
          data: { id: idOf(n.entity), label: `${n.entity.type}\n${n.entity.key}`, size: sizeOf(graph.edges.filter((e) => e.from === idOf(n.entity) || e.to === idOf(n.entity)).length), color: colors.get(systemOf(catalog.value, n.entity.type) ?? "") ?? noSystem },
        })),
        ...graph.edges.map((e) => ({ group: "edges" as const, data: { id: `${e.relation}:${e.from}->${e.to}`, source: e.from, target: e.to, label: e.relation } })),
      ],
      // A walk is a tree from where it started: each relation one level further out.
      layout: { name: "breadthfirst", directed: true, roots: [...graph.nodes.values()].filter((n) => n.depth === 0).map((n) => idOf(n.entity)), spacingFactor: 1.4, padding: 48, animate: false },
      maxZoom: 1.5,
      minZoom: 0.2,
      style: [
        { selector: "node", style: { "background-color": "data(color)", width: "data(size)", height: "data(size)", label: "data(label)", color: ink, "font-size": 11, "text-wrap": "wrap", "text-valign": "bottom", "text-margin-y": 4 } },
        { selector: "node:selected", style: { "border-width": 3, "border-color": ink } },
        { selector: "edge", style: { width: 1.5, "line-color": "#9aa2ad", "target-arrow-color": "#9aa2ad", "target-arrow-shape": "triangle", "curve-style": "bezier", label: "data(label)", "font-size": 10, color: ink, "text-rotation": "autorotate" } },
      ],
    });
    cy.on("tap", "node", (event) => setSelected(event.target.id()));
    drawn.current = cy;
    return () => {
      drawn.current = null;
      cy.destroy();
    };
  }, [graph, catalog, colors]);

  // Selecting marks the node where it stands; the drawing is laid out again only when a step changes the graph.
  useEffect(() => {
    drawn.current?.nodes().unselect();
    if (selected) {
      drawn.current?.getElementById(selected).select();
    }
  }, [selected, graph]);

  if (catalog === null) {
    return <p>Loading the catalog…</p>;
  }
  if (!catalog.ok) {
    return <p role="alert">{"problem" in catalog ? catalog.problem : "The catalog could not be read."}</p>;
  }
  const declared = catalog.value;

  async function walk(from: EntityKey, relation: string | null, to: string) {
    setBusy(true);
    setProblem(null);
    const answer = await api.lookup({ start: from, path: relation === null ? [] : [relation], read: fieldsOf(declared, to) });
    setBusy(false);
    if (!answer.ok) {
      setProblem("problem" in answer ? answer.problem : answer.status === 403 ? "Only builders walk a local graph." : "Your session has ended — sign in again.");
      return;
    }
    setGraph((g) => step(relation === null ? empty() : g, from, relation, answer.value));
    if (relation === null) {
      setSelected(idOf(from));
    }
  }

  const node = selected ? graph.nodes.get(selected) : undefined;
  const lastStep = graph.steps.at(-1);
  return (
    <section>
      <h1>Local graph</h1>
      <form
        className="start"
        onSubmit={(e) => {
          e.preventDefault();
          if (type && key.trim()) {
            walk({ type, key: key.trim() }, null, type);
          }
        }}
      >
        <label>
          Start at{" "}
          <select value={type} onChange={(e) => setType(e.target.value)}>
            {declared.declarations
              .filter((d) => d.kind === "entityType")
              .map((d) => (
                <option key={d.name}>{d.name}</option>
              ))}
          </select>
        </label>
        <label>
          key <input value={key} onChange={(e) => setKey(e.target.value)} placeholder="the entity's key" />
        </label>
        <button className="button" disabled={busy || !key.trim()}>
          Look up
        </button>
      </form>
      {problem && <p role="alert">{problem}</p>}
      {lastStep?.refusal && <p className="refusal">{refusalText(lastStep.refusal)}</p>}
      {lastStep && lastStep.partitions.some((p) => p.blocked || p.incomplete) && (
        <p className="refusal">
          Read from edges kept for your groups:{" "}
          {lastStep.partitions.map((p) => `${p.name}${p.blocked ? " (blocked)" : p.incomplete ? " (incomplete)" : ""}${p.asOf ? ` as of ${p.asOf}` : ""}`).join(", ")}
        </p>
      )}
      {graph.nodes.size > 0 && (
        <div className="graph">
          <div ref={canvas} className="canvas" role="img" aria-label="The local graph" />
          <aside className="detail">
            {node ? (
              <>
                <h2>
                  {node.entity.type} {node.entity.key}
                </h2>
                <p className="muted">
                  {systemOf(declared, node.entity.type)} · {node.depth} {node.depth === 1 ? "relation" : "relations"} from the start
                </p>
                <dl>
                  {node.values.map((v) => (
                    <div key={v.field}>
                      <dt>{v.field}</dt>
                      <dd className={v.fieldKind === "freeText" ? "free" : undefined}>{v.value === null ? <span className="muted">empty</span> : String(v.value)}</dd>
                      <dd className="muted source">{v.sources.map((s) => `${s.system} · ${s.table} · ${s.record}`).join("; ")}</dd>
                    </div>
                  ))}
                </dl>
                {node.depth < maxDepth ? (
                  <div className="walks">
                    {walksFrom(declared, node.entity.type).map((w) => (
                      <button key={w.relation} className="tab" disabled={busy} onClick={() => walk(node.entity, w.relation, w.to)}>
                        {w.backwards ? "←" : "→"} {w.relation} ({w.to})
                      </button>
                    ))}
                    {walksFrom(declared, node.entity.type).length === 0 && <p className="muted">No relation leaves {node.entity.type}.</p>}
                  </div>
                ) : (
                  <p className="muted">Three relations from the start — a local graph walks no further.</p>
                )}
              </>
            ) : (
              <p className="muted">Pick a node to read it and walk on.</p>
            )}
          </aside>
        </div>
      )}
    </section>
  );
}
