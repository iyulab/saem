import { useCallback, useEffect, useRef, useState } from "react";
import { api, whyNot, type Answer, type Catalog, type Job, type JobKind } from "./api";
import { ago, asks, cancelWarning, checkReport, copyFacts, countsStale, fitIdentities, fitSays, isRunning, outcomeOf, runningJob, settle, type Settled, statusOf, summary, took, violations } from "./jobs";

const POLL_MS = 2000;

/**
 * The groups, side by side: for each, what its copy of the relations holds and when it was last refreshed, what a builder may
 * ask next, and what the last thing asked said. The console only relays — a group's executor holds its sources and its store —
 * so what appears is what the executor printed, except a check's lines and cited records, which the console leaves out.
 */
export function GroupsView() {
  const [groups, setGroups] = useState<Answer<Array<{ group: string }>> | null>(null);
  const [catalog, setCatalog] = useState<Answer<Catalog> | null>(null);

  useEffect(() => {
    api.catalog().then(setCatalog);
    api.executors().then(setGroups);
  }, []);

  if (groups === null) {
    return <p>Loading…</p>;
  }
  if (!groups.ok) {
    return <p role="alert">{whyNot(groups, "Only builders ask a group's executor.")}</p>;
  }
  const fits = catalog?.ok ? fitIdentities(catalog.value) : [];
  return (
    <section className="groups">
      <h1>Groups</h1>
      <p className="lead">
        Each group keeps its own copy of the relations its account can see in the sources. A person is answered from their group's copy — and still only with what the source shows that person.
      </p>
      {groups.value.length === 0 ? (
        <p className="muted">
          No executor is set. Name each group's <code>saem-materialize serve</code> in <code>Saem:Console:Executors</code>.
        </p>
      ) : (
        <div className="group-cards">
          {groups.value.map((g) => (
            <GroupCard key={g.group} group={g.group} fits={fits} />
          ))}
        </div>
      )}
      <aside className="note">
        <h2>How a question is answered</h2>
        <p>
          A question that goes against a relation — <em>which work orders make this item?</em> — is answered from the asker's group copy, and says which copy and when it was read. Someone in no group is told there is no copy to read; questions that follow a relation forward still answer.
        </p>
        <dl>
          <dt>Refresh (whole pass)</dt>
          <dd>reads everything the group's account may see and makes the copy match it.</dd>
          <dt>Only what changed (changed-rows pass)</dt>
          <dd>reads just the rows the source changed since the last pass. It cannot see a row that was retired — a whole pass does.</dd>
          <dt>Wrote · kept · retired</dt>
          <dd>how many links the pass added or corrected, left as they were, and took out because the source no longer has them.</dd>
        </dl>
      </aside>
    </section>
  );
}

function GroupCard({ group, fits }: { group: string; fits: string[] }) {
  const [settled, setSettled] = useState<Settled | null>(null);
  const jobs = settled?.jobs ?? null;
  const [open, setOpen] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<{ kind: JobKind; identity?: string } | null>(null);
  const [stopping, setStopping] = useState<Job | null>(null);
  const [identity, setIdentity] = useState(fits[0] ?? "");
  const [refused, setRefused] = useState<string | null>(null);
  const [now, setNow] = useState(() => new Date());
  const readCountsFor = useRef<string | null>(null);

  const load = useCallback(() => {
    api.jobs(group).then((answer) => {
      setSettled((before) => settle(before, answer));
      setNow(new Date());
    });
  }, [group]);

  useEffect(load, [load]);

  const list = jobs?.ok ? jobs.value : [];
  const running = runningJob(list);
  useEffect(() => {
    if (!running) {
      return;
    }
    const timer = setInterval(load, POLL_MS);
    return () => clearInterval(timer);
  }, [running?.id, load]);

  // A copy's counts are one read-only ask away: read them when the card is first shown, and again after the group's copy was written.
  useEffect(() => {
    const stale = jobs?.ok ? countsStale(list) : null;
    if (stale && stale !== readCountsFor.current && !running) {
      readCountsFor.current = stale;
      api.startJob(group, "status").then(load);
    }
  }, [jobs, running, list, group, load]);

  const ask = (kind: JobKind, forIdentity?: string) => {
    setConfirm(null);
    setRefused(null);
    api.startJob(group, kind, forIdentity ? { identity: forIdentity } : undefined).then((answer) => {
      if (answer.ok) {
        setOpen(answer.value.id);
      } else {
        setRefused(whyNot(answer, "The console refused."));
      }
      load();
    });
  };

  if (jobs === null) {
    return <article className="group-card">Loading…</article>;
  }
  if (!jobs.ok) {
    return (
      <article className="group-card">
        <h2>{group}</h2>
        <p role="alert">{whyNot(jobs, "The executor could not be asked.")}</p>
      </article>
    );
  }

  const facts = copyFacts(list);
  const last = list.find((j) => !isRunning(j) && j.kind !== "status");
  const outcome = last ? outcomeOf(last, now) : null;
  const shown = list.find((j) => j.id === open) ?? list[0];
  const primary = asks.filter((a) => a.kind === "pass" || a.kind === "changed-pass" || a.kind === "verify");
  const secondary = asks.filter((a) => a.kind === "check" || a.kind === "status");
  const button = (a: (typeof asks)[number], first: boolean) => (
    <button
      key={a.kind}
      className={first ? "button" : "outline"}
      title={`${a.says} (${a.term})`}
      disabled={running !== undefined}
      onClick={() => (a.writes ? setConfirm({ kind: a.kind }) : ask(a.kind))}
    >
      {a.label}
    </button>
  );
  return (
    <article className="group-card" aria-label={`Group ${group}`}>
      <h2>{group}</h2>
      <p className="muted small">Reads the source with its own account and keeps its own copy.</p>
      <dl className="facts">
        <dt>Last refresh</dt>
        <dd>
          {facts.asOf ? `${new Date(facts.asOf).toLocaleString()} · ${ago(facts.asOf, now)}` : "never — nothing has been read into this copy yet"}
          {facts.agrees === true && <span className="badge ok">matches the source</span>}
          {facts.agrees === false && <span className="badge warn">differs from the source</span>}
        </dd>
        <dt>Holds</dt>
        <dd>{facts.edges === null ? "reading…" : `${facts.edges} link${facts.edges === 1 ? "" : "s"} · ${facts.relations} relation${facts.relations === 1 ? "" : "s"}`}</dd>
        <dt>Answers</dt>
        <dd>
          people whose sign-in names <strong>{group}</strong>
        </dd>
      </dl>
      <div className="actions-row">
        {primary.map((a, i) => button(a, i === 0))}
        {running && (
          <button className="link" onClick={() => (cancelWarning(running) === null ? api.cancelJob(group, running.id).then(load) : setStopping(running))}>
            Cancel the running {running.kind} <span className="muted">({running.requestedBy})</span>
          </button>
        )}
      </div>
      <details className="inline">
        <summary>More options</summary>
        <div className="actions-row">
          {secondary.map((a) => button(a, false))}
          {fits.length > 0 && (
            <span>
              <select value={identity || fits[0]} onChange={(e) => setIdentity(e.target.value)} aria-label="Identity to fit">
                {fits.map((name) => (
                  <option key={name}>{name}</option>
                ))}
              </select>{" "}
              <button className="outline" title={fitSays} disabled={running !== undefined} onClick={() => setConfirm({ kind: "fit", identity: identity || fits[0] })}>
                Fit
              </button>
            </span>
          )}
        </div>
      </details>
      {settled?.unreached && (
        <p className="muted" role="status">
          {settled.unreached} Showing what was last read; asking again.
        </p>
      )}
      {stopping && running?.id === stopping.id && (
        <p className="refusal" role="alertdialog">
          {cancelWarning(stopping)}{" "}
          <button
            className="button"
            onClick={() => {
              setStopping(null);
              api.cancelJob(group, stopping.id).then(load);
            }}
          >
            Stop it
          </button>{" "}
          <button className="link" onClick={() => setStopping(null)}>
            Keep it running
          </button>
        </p>
      )}
      {confirm && (
        <p className="refusal" role="alertdialog">
          {confirm.kind === "fit" ? `${fitSays} (${confirm.identity})` : asks.find((a) => a.kind === confirm.kind)?.says} It writes to <strong>{group}</strong>'s copy.{" "}
          <button className="button" onClick={() => ask(confirm.kind, confirm.identity)}>
            Run it
          </button>{" "}
          <button className="link" onClick={() => setConfirm(null)}>
            Not now
          </button>
        </p>
      )}
      {refused && (
        <p className="refusal" role="alert">
          {refused}
        </p>
      )}
      {running && (
        <p className="result" role="status">
          <strong>{asks.find((a) => a.kind === running.kind)?.label ?? running.kind}</strong> · asked by {running.requestedBy}
          <br />
          <span className="muted">Running… {took(running, now)}</span>
        </p>
      )}
      {!running && outcome && (
        <p className={`result ${outcome.tone}`} role="status">
          <strong>{outcome.title}</strong>
          <br />
          {outcome.facts.length > 0 && <span className="figures">{outcome.facts.join(" · ")} · </span>}
          <span className="muted">done in {outcome.took}</span>
          {last?.kind === "verify" && outcome.tone === "warn" && (
            <>
              <br />
              <span className="muted small">The copy no longer matches the source. “Refresh from the source” brings it back.</span>
            </>
          )}
          {last && (last.kind === "pass" || last.kind === "changed-pass") && outcome.facts.length === 3 && (
            <>
              <br />
              <span className="muted small">links added or corrected · left as they were · taken out because the source no longer has them</span>
            </>
          )}
        </p>
      )}
      {!running && !outcome && <p className="muted">Nothing has been asked of this group yet.</p>}
      {list.length > 0 && (
        <details className="inline">
          <summary>History ({list.filter((j) => j.kind !== "status").length})</summary>
          <div className="graph">
            <table>
              <thead>
                <tr>
                  <th>Job</th>
                  <th>Started</th>
                  <th>Took</th>
                </tr>
              </thead>
              <tbody>
                {list.map((j) => (
                  <tr key={j.id} className={j.state === "failed" ? "worse" : undefined}>
                    <td>
                      <button className="link" onClick={() => setOpen(j.id)} aria-current={j.id === shown?.id}>
                        {summary(j)}
                      </button>
                    </td>
                    <td className="muted">{new Date(j.startedAt).toLocaleString()}</td>
                    <td className="muted">{took(j, now)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            {shown && <JobDetail job={shown} />}
          </div>
        </details>
      )}
    </article>
  );
}

function JobDetail({ job }: { job: Job }) {
  const broken = violations(job);
  const report = checkReport(job);
  const status = statusOf(job);
  return (
    <aside className="detail" aria-label="Job">
      <h2>{summary(job)}</h2>
      {isRunning(job) && <p className="muted">Running…</p>}
      {job.outputWithheld ? (
        <p className="muted">A check's lines cite records the group's account read; they stay with the executor.</p>
      ) : (
        <pre className="output">{job.output || "(nothing printed yet)"}</pre>
      )}
      {status && (
        <>
          <p className="muted">
            {status.asOf ? `Edges are as of the pass that began reading ${new Date(status.asOf).toLocaleString()}.` : "No pass has filed a time yet."}
          </p>
          <table>
            <thead>
              <tr>
                <th>Relation</th>
                <th>Edges</th>
                <th>Read on from</th>
              </tr>
            </thead>
            <tbody>
              {status.relations.map((r) => (
                <tr key={r.relation}>
                  <td>{r.relation}</td>
                  <td>{r.edges}</td>
                  <td className="muted">{r.watermark ?? "read whole"}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {status.identities.length > 0 && (
            <table>
              <thead>
                <tr>
                  <th>Identity</th>
                  <th>Things</th>
                  <th>Members</th>
                  <th>Awaiting a person</th>
                </tr>
              </thead>
              <tbody>
                {status.identities.map((i) => (
                  <tr key={i.identity}>
                    <td>{i.identity}</td>
                    <td>{i.things}</td>
                    <td>{i.members}</td>
                    <td>{i.awaiting}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </>
      )}
      {job.outputTruncated && <p className="muted">The executor kept the first part of what the job printed.</p>}
      {report && (
        <>
          <p>{report.holds ? "Every declaration holds." : `${broken.length} finding(s) say a declaration does not hold:`}</p>
          <ul>
            {broken.map((f) => (
              <li key={`${f.type}.${f.relation}:${f.kind}`}>
                {f.relation ? `${f.type}.${f.relation}` : f.type} [{f.kind}] {f.what}
                {f.recordsCited > 0 ? ` — ${f.recordsCited} record(s) cited in the executor's report` : ""}
              </li>
            ))}
          </ul>
        </>
      )}
    </aside>
  );
}
