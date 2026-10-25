import { lazy, Suspense, useEffect, useState } from "react";
import { api, signInAddress, type Answer, type Caller } from "./api";
import { CatalogView } from "./CatalogView";

// The graph library is most of the page's weight: it loads when a graph screen is first opened, not before.
const GraphView = lazy(() => import("./GraphView").then((m) => ({ default: m.GraphView })));

type Screen = "catalog" | "graph";

const screens: ReadonlyArray<{ screen: Screen; title: string }> = [
  { screen: "catalog", title: "Catalog" },
  { screen: "graph", title: "Knowledge graph" },
];

/** The console: who is signed in, the screens their role opens, and the screen in front of them. */
export function App() {
  const [me, setMe] = useState<Answer<Caller> | null>(null);
  const [screen, setScreen] = useState<Screen>("catalog");

  useEffect(() => {
    api.me().then(setMe);
  }, []);

  if (me === null) {
    return <main className="center">Loading…</main>;
  }
  if (!me.ok) {
    return <SignedOut status={me.status} problem={"problem" in me ? me.problem : undefined} />;
  }

  return (
    <div className="shell">
      <header className="bar">
        <strong className="brand">saem console</strong>
        <nav>
          {screens.map((s) => (
            <button key={s.screen} className={s.screen === screen ? "tab active" : "tab"} onClick={() => setScreen(s.screen)}>
              {s.title}
            </button>
          ))}
        </nav>
        <span className="who">
          {me.value.subject} · {me.value.roles.join(", ")}
        </span>
        <button className="link" onClick={() => api.logout().then(() => window.location.assign("/"))}>
          Sign out
        </button>
      </header>
      <main>
        {screen === "catalog" && <CatalogView />}
        {screen === "graph" && (
          <Suspense fallback={<p>Loading the graph…</p>}>
            <GraphView />
          </Suspense>
        )}
      </main>
    </div>
  );
}

function SignedOut({ status, problem }: { status: number; problem?: string }) {
  return (
    <main className="center">
      <h1>saem console</h1>
      {status === 401 && (
        <>
          <p>Sign in with your organization's account to keep the ontology.</p>
          <a className="button" href={signInAddress(window.location.pathname)}>
            Sign in
          </a>
        </>
      )}
      {status === 403 && <p>You are signed in, but none of your groups holds a console role. Ask whoever maps groups to roles.</p>}
      {status !== 401 && status !== 403 && <p role="alert">{problem}</p>}
    </main>
  );
}
