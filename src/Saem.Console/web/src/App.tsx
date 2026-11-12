import { lazy, Suspense, useEffect, useState } from "react";
import { api, signInAddress, type Answer, type Caller, type EntityKey } from "./api";
import { CatalogView } from "./CatalogView";
import { CheckView } from "./CheckView";
import { DraftView } from "./DraftView";
import { ReviewView } from "./ReviewView";
import { GroupsView } from "./GroupsView";
import { JudgmentsView } from "./JudgmentsView";
import { OverviewView } from "./OverviewView";
import { tabsFor, type Screen } from "./screens";

// The graph library is most of the page's weight: it loads when a graph screen is first opened, not before.
const GraphView = lazy(() => import("./GraphView").then((m) => ({ default: m.GraphView })));
const LocalGraphView = lazy(() => import("./LocalGraphView").then((m) => ({ default: m.LocalGraphView })));

/** The console: who is signed in, the screens their role opens, and the screen in front of them. */
export function App() {
  const [me, setMe] = useState<Answer<Caller> | null>(null);
  const [screen, setScreen] = useState<Screen>("overview");
  const [localFrom, setLocalFrom] = useState<{ entity: EntityKey; at: number } | undefined>();

  useEffect(() => {
    api.me().then(setMe);
  }, []);

  if (me === null) {
    return <main className="center">Loading…</main>;
  }
  if (!me.ok) {
    return <SignedOut status={me.status} problem={"problem" in me ? me.problem : undefined} />;
  }

  const { main, more } = tabsFor(me.value.roles);
  const moreActive = more.some((s) => s.screen === screen);
  const tab = (s: { screen: Screen; title: string }) => (
    <button
      key={s.screen}
      className={s.screen === screen ? "tab active" : "tab"}
      onClick={() => {
        setLocalFrom(undefined);
        setScreen(s.screen);
      }}
    >
      {s.title}
    </button>
  );

  return (
    <div className="shell">
      <header className="bar">
        <strong className="brand">saem console</strong>
        <nav>
          {main.map(tab)}
          {more.length > 0 && (
            // Remounted when the screen in front moves in or out of the group, so it opens for a screen it holds and closes otherwise.
            <details key={moreActive ? "open" : "closed"} className="more" open={moreActive}>
              <summary>More</summary>
              {more.map(tab)}
            </details>
          )}
        </nav>
        <span className="who">
          {me.value.subject} · {me.value.roles.join(", ")}
        </span>
        <button className="link" onClick={() => api.logout().then(() => window.location.assign("/"))}>
          Sign out
        </button>
      </header>
      <main>
        {screen === "overview" && <OverviewView onOpenCheck={me.value.roles.includes("builder") ? () => setScreen("check") : undefined} />}
        {screen === "catalog" && <CatalogView />}
        {screen === "review" && <ReviewView onOpenDraft={() => setScreen("draft")} onOpenCatalog={() => setScreen("catalog")} />}
        {screen === "draft" && <DraftView onOpenReview={() => setScreen("review")} onOpenCatalog={() => setScreen("catalog")} />}
        {screen === "check" && <CheckView />}
        {screen === "judgments" && (
          <JudgmentsView
            onOpenLocal={(entity) => {
              setLocalFrom({ entity, at: Date.now() });
              setScreen("local");
            }}
          />
        )}
        {screen === "groups" && <GroupsView />}
        {screen === "local" && (
          <Suspense fallback={<p>Loading…</p>}>
            <LocalGraphView key={localFrom?.at} from={localFrom?.entity} />
          </Suspense>
        )}
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
      {status === 403 && (
        <>
          <p>You are signed in, but none of your groups holds a console role. Ask whoever maps groups to roles.</p>
          <button className="link" onClick={() => api.logout().then(() => window.location.assign("/"))}>
            Sign out
          </button>
        </>
      )}
      {status !== 401 && status !== 403 && <p role="alert">{problem}</p>}
    </main>
  );
}
