import { useCallback, useEffect, useState } from "react";
import { api, type Answer, type Catalog, type Job, type JobKind } from "./api";
import { asks, checkReport, fitIdentities, fitSays, isRunning, runningJob, statusOf, summary, took, violations } from "./jobs";

const POLL_MS = 2000;

/**
 * The groups' executors: what each has been asked for and what it said, and what a builder may ask next. The console
 * only relays — a group's executor holds its sources and its store — so what appears is what the executor printed,
 * except a check's lines and cited records, which the console leaves out.
 */
export function GroupsView() {
  const [groups, setGroups] = useState<Answer<Array<{ group: string }>> | null>(null);
  const [group, setGroup] = useState<string | null>(null);
  const [catalog, setCatalog] = useState<Answer<Catalog> | null>(null);

  useEffect(() => {
    api.catalog().then(setCatalog);
    api.executors().then((answer) => {
      setGroups(answer);
      if (answer.ok && answer.value.length > 0) {
        setGroup(answer.value[0].group);
      }
    });
  }, []);

  if (groups === null) {
    return <p>Loading…</p>;
  }
  if (!groups.ok) {
    return <p role="alert">{"problem" in groups ? groups.problem : "Only builders ask a group's executor."}</p>;
  }
  if (groups.value.length === 0) {
    return (
      <section>
        <h1>Groups</h1>
        <p className="muted">
          No executor is set. Name each group's <code>saem-materialize serve</code> in <code>Saem:Console:Executors</code>.
        </p>
      </section>
    );
  }

  return (
    <section>
      <h1>Groups</h1>
      <div className="start">
        <label>
          Group{" "}
          <select value={group ?? ""} onChange={(e) => setGroup(e.target.value)} aria-label="Group">
            {groups.value.map((g) => (
              <option key={g.group}>{g.group}</option>
            ))}
          </select>
        </label>
      </div>
      {group && <Executor key={group} group={group} fits={catalog?.ok ? fitIdentities(catalog.value) : []} />}
    </section>
  );
}

function Executor({ group, fits }: { group: string; fits: string[] }) {
  const [jobs, setJobs] = useState<Answer<Job[]> | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<{ kind: JobKind; identity?: string } | null>(null);
  const [identity, setIdentity] = useState(fits[0] ?? "");
  const [refused, setRefused] = useState<string | null>(null);
  const [now, setNow] = useState(() => new Date());

  const load = useCallback(() => {
    api.jobs(group).then((answer) => {
      setJobs(answer);
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

  const ask = (kind: JobKind, forIdentity?: string) => {
    setConfirm(null);
    setRefused(null);
    api.startJob(group, kind, forIdentity ? { identity: forIdentity } : undefined).then((answer) => {
      if (answer.ok) {
        setOpen(answer.value.id);
      } else {
        setRefused("problem" in answer ? answer.problem : "The console refused.");
      }
      load();
    });
  };

  if (jobs === null) {
    return <p>Loading…</p>;
  }
  if (!jobs.ok) {
    return <p role="alert">{"problem" in jobs ? jobs.problem : "The executor could not be asked."}</p>;
  }

  const shown = list.find((j) => j.id === open) ?? list[0];
  return (
    <>
      <div className="start">
        {asks.map((a) => (
          <button
            key={a.kind}
            className="button"
            title={a.says}
            disabled={running !== undefined}
            onClick={() => (a.writes ? setConfirm({ kind: a.kind }) : ask(a.kind))}
          >
            {a.title}
          </button>
        ))}
        {fits.length > 0 && (
          <span>
            <select value={identity || fits[0]} onChange={(e) => setIdentity(e.target.value)} aria-label="Identity to fit">
              {fits.map((name) => (
                <option key={name}>{name}</option>
              ))}
            </select>{" "}
            <button className="button" title={fitSays} disabled={running !== undefined} onClick={() => setConfirm({ kind: "fit", identity: identity || fits[0] })}>
              Fit
            </button>
          </span>
        )}
        {running && (
          <button className="link" onClick={() => api.cancelJob(group, running.id).then(load)}>
            Cancel the running {running.kind}
          </button>
        )}
      </div>
      {confirm && (
        <p className="refusal" role="alertdialog">
          {confirm.kind === "fit" ? `${fitSays} (${confirm.identity})` : asks.find((a) => a.kind === confirm.kind)?.says} It writes to <strong>{group}</strong>'s store.{" "}
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
      {list.length === 0 && <p className="muted">This executor has not run a job since it started.</p>}
      {list.length > 0 && (
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
      )}
    </>
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
