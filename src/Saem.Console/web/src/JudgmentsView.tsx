import { useCallback, useEffect, useState } from "react";
import { api, type Answer, type Job } from "./api";
import { awaiting, latestCandidates, pairKey, runningJob } from "./jobs";

const POLL_MS = 2000;

/**
 * The pairs a person is yet to judge, from a group's executor: an identity a person settles lists what its rule found,
 * and a builder says each pair is one thing or two. The console shows IRIs and scores only — it reads no record, and a
 * judgment is filed by the executor in the name of the one who asked. The group's things change on its next pass.
 */
export function JudgmentsView() {
  const [groups, setGroups] = useState<Answer<Array<{ group: string }>> | null>(null);
  const [group, setGroup] = useState<string | null>(null);

  useEffect(() => {
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
        <h1>Judgments</h1>
        <p className="muted">
          No executor is set. Name each group's <code>saem-materialize serve</code> in <code>Saem:Console:Executors</code>.
        </p>
      </section>
    );
  }

  return (
    <section>
      <h1>Judgments</h1>
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
      {group && <Candidates key={group} group={group} />}
    </section>
  );
}

function Candidates({ group }: { group: string }) {
  const [jobs, setJobs] = useState<Answer<Job[]> | null>(null);
  const [judged, setJudged] = useState<ReadonlySet<string>>(new Set());
  const [refused, setRefused] = useState<string | null>(null);

  const load = useCallback(() => api.jobs(group).then(setJobs), [group]);
  useEffect(() => {
    load();
  }, [load]);

  const list = jobs?.ok ? jobs.value : [];
  const running = runningJob(list);
  useEffect(() => {
    if (!running) {
      return;
    }
    const timer = setInterval(load, POLL_MS);
    return () => clearInterval(timer);
  }, [running?.id, load]);

  if (jobs === null) {
    return <p>Loading…</p>;
  }
  if (!jobs.ok) {
    return <p role="alert">{"problem" in jobs ? jobs.problem : "The executor could not be asked."}</p>;
  }

  const listed = latestCandidates(list);
  const left = listed ? awaiting(listed.pairs, judged) : [];

  const refresh = () => {
    setRefused(null);
    setJudged(new Set());
    api.startJob(group, "candidates").then((answer) => {
      if (!answer.ok) {
        setRefused("problem" in answer ? answer.problem : "The console refused.");
      }
      load();
    });
  };

  const judge = (c: (typeof left)[number], same: boolean) => {
    setRefused(null);
    api.startJob(group, "judge", { identity: c.identity, first: c.first, second: c.second, same }).then((answer) => {
      if (answer.ok) {
        setJudged((before) => new Set(before).add(pairKey(c)));
      } else {
        setRefused("problem" in answer ? answer.problem : "The console refused.");
      }
      load();
    });
  };

  return (
    <>
      <div className="start">
        <button className="button" disabled={running !== undefined} onClick={refresh} title="Read the pairs no one has judged from the group's store.">
          {listed ? "Read the pairs again" : "Read the pairs"}
        </button>
        {running && <span className="muted">The executor is running a {running.kind}…</span>}
      </div>
      {refused && (
        <p className="refusal" role="alert">
          {refused}
        </p>
      )}
      {!listed && <p className="muted">The pairs have not been read since this executor started.</p>}
      {listed && (
        <>
          <p className="muted">
            Read {new Date(listed.job.finishedAt ?? listed.job.startedAt).toLocaleString()} from the group's store as of its last pass
            {judged.size > 0 ? ` · ${judged.size} judged since — they join the group's things on its next pass` : ""}.
          </p>
          {left.length === 0 && <p>No pair waits for a person's judgment.</p>}
          {left.length > 0 && (
            <div className="graph">
              <table>
                <thead>
                  <tr>
                    <th>Identity</th>
                    <th>One entity</th>
                    <th>The other</th>
                    <th>Score</th>
                    <th>Is it one thing?</th>
                  </tr>
                </thead>
                <tbody>
                  {left.map((c) => (
                    <tr key={pairKey(c)}>
                      <td>{c.identity}</td>
                      <td><code>{c.first}</code></td>
                      <td><code>{c.second}</code></td>
                      <td className="muted">{c.score}</td>
                      <td>
                        <button className="button" disabled={running !== undefined} onClick={() => judge(c, true)}>
                          Same
                        </button>{" "}
                        <button className="button" disabled={running !== undefined} onClick={() => judge(c, false)}>
                          Different
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </>
  );
}
