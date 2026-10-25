import cytoscape from "cytoscape";
import { useEffect, useRef, useState } from "react";
import { api, type Answer, type DeclarationGraph } from "./api";
import { elements, neighborhood, systems } from "./graph";

/**
 * The declaration graph: every entity type and grouping, sized by how many declarations touch it and colored by the
 * system it stands on. Declarations only — no source is read to draw it.
 */
export function GraphView() {
  const [graph, setGraph] = useState<Answer<DeclarationGraph> | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const canvas = useRef<HTMLDivElement>(null);

  useEffect(() => {
    api.graph().then(setGraph);
  }, []);

  useEffect(() => {
    if (!graph?.ok || !canvas.current) {
      return;
    }
    const ink = getComputedStyle(document.documentElement).getPropertyValue("--fg").trim() || "#1d1f23";
    const cy = cytoscape({
      container: canvas.current,
      elements: elements(graph.value),
      layout: { name: "cose", animate: false, padding: 48, idealEdgeLength: () => 140, nodeRepulsion: () => 12000 },
      // A small catalog fits the canvas without being blown up past reading size.
      maxZoom: 1.5,
      minZoom: 0.2,
      style: [
        {
          selector: "node",
          style: {
            "background-color": "data(color)",
            width: "data(size)",
            height: "data(size)",
            label: "data(label)",
            color: ink,
            "font-size": 12,
            "text-valign": "bottom",
            "text-margin-y": 4,
          },
        },
        { selector: "node[kind = 'grouping']", style: { shape: "round-rectangle", "background-opacity": 0.35, "border-width": 2, "border-color": "data(color)" } },
        { selector: "node:selected", style: { "border-width": 3, "border-color": ink } },
        {
          selector: "edge",
          style: { width: 1.5, "line-color": "#9aa2ad", "target-arrow-color": "#9aa2ad", "curve-style": "bezier", label: "data(label)", "font-size": 10, color: ink, "text-rotation": "autorotate" },
        },
        { selector: "edge[kind = 'relation']", style: { "target-arrow-shape": "triangle" } },
        { selector: "edge[kind = 'identity']", style: { "line-style": "dashed", "line-color": "#c2417d" } },
        { selector: "edge[kind = 'member']", style: { "line-style": "dotted" } },
      ],
    });
    cy.on("tap", "node", (event) => setSelected(event.target.id()));
    cy.on("tap", (event) => event.target === cy && setSelected(null));
    return () => cy.destroy();
  }, [graph]);

  if (graph === null) {
    return <p>Loading the graph…</p>;
  }
  if (!graph.ok) {
    return <p role="alert">{"problem" in graph ? graph.problem : "The graph could not be read."}</p>;
  }

  const node = graph.value.nodes.find((n) => n.name === selected);
  return (
    <section>
      <h1>Knowledge graph</h1>
      <p className="muted">
        Declarations of version <code>{graph.value.version}</code> · {graph.value.nodes.length} nodes · {graph.value.edges.length} edges
      </p>
      <ul className="legend" aria-label="Systems">
        {systems(graph.value).map((s) => (
          <li key={s.system}>
            <span className="swatch" style={{ background: s.color }} /> {s.system}
          </li>
        ))}
        <li>▭ grouping · → relation · ┄ identity · ⋯ membership</li>
      </ul>
      <div className="graph">
        <div ref={canvas} className="canvas" role="img" aria-label="The declaration graph" />
        <aside className="detail">
          {node ? (
            <>
              <h2>{node.name}</h2>
              <p className="muted">
                {node.kind === "grouping" ? "grouping" : `entity type on ${node.system}`} · {node.degree} {node.degree === 1 ? "edge" : "edges"}
              </p>
              <ul>
                {neighborhood(graph.value, node.name).map(({ edge, other, outgoing }) => (
                  <li key={`${edge.kind}:${edge.name}:${edge.from}->${edge.to}`}>
                    {edge.kind} <strong>{edge.name}</strong> {outgoing ? "→" : "←"} {other}
                  </li>
                ))}
              </ul>
            </>
          ) : (
            <p className="muted">Pick a node to see what touches it.</p>
          )}
        </aside>
      </div>
    </section>
  );
}
