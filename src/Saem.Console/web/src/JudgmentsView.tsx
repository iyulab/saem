import { Fragment, useCallback, useEffect, useState } from "react";
import { api, whyNot, type Answer, type Catalog, type EntityKey, type Job, type PairCandidate } from "./api";
import { compare, sideOf, type CompareRow } from "./compare";
import { awaiting, judgedPairs, judgedSince, judgments, latestCandidates, pairKey, runningJob, settle, type Settled } from "./jobs";
import { fieldsOf } from "./localGraph";

const POLL_MS = 2000;

/**
 * The pairs a person is yet to judge, from a group's executor: an identity a person settles lists what its rule found,
 * and a builder says each pair is one thing or two. The executor's list holds IRIs and scores only. "Compare" reads the two
 * entities' declared fields as the person judging — the way "Look" does, with the same lookup and the same rights — and
 * lays them side by side; what they cannot read they do not see. A judgment is filed by the executor in the name of the
 * one who asked. The group's things change on its next pass.
 */
export function JudgmentsView({ onOpenLocal }: { onOpenLocal: (entity: EntityKey) => void }) {
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
    return <p role="alert">{whyNot(groups, "Only builders ask a group's executor.")}</p>;
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
      {group && <Candidates key={group} group={group} onOpenLocal={onOpenLocal} />}
    </section>
  );
}

function Candidates({ group, onOpenLocal }: { group: string; onOpenLocal: (entity: EntityKey) => void }) {
  const [settled, setSettled] = useState<Settled | null>(null);
  const jobs = settled?.jobs ?? null;
  // Each judgment a person filed this session: the pair, and the job that carries it. A pair is judged when that job succeeded, not when it was accepted.
  const [filed, setFiled] = useState<ReadonlyMap<string, string>>(new Map());
  // The latest pairs the executor listed, kept: it remembers only its latest jobs, and a list is not lost because newer jobs pushed it out.
  const [kept, setKept] = useState<{ job: Job; pairs: PairCandidate[] } | null>(null);
  const [refused, setRefused] = useState<string | null>(null);
  const [compared, setCompared] = useState<ReadonlyMap<string, Comparison>>(new Map());
  const [catalog, setCatalog] = useState<Catalog | null>(null);

  const load = useCallback(() => api.jobs(group).then((answer) => setSettled((before) => settle(before, answer))), [group]);
  useEffect(() => {
    load();
  }, [load]);

  const list = jobs?.ok ? jobs.value : [];
  const running = runningJob(list);
  const latest = latestCandidates(list);
  useEffect(() => {
    if (latest) {
      setKept(latest);
    }
  }, [latest?.job.id]);
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
    return <p role="alert">{whyNot(jobs, "The executor could not be asked.")}</p>;
  }

  const listed = latest ?? kept;
  const outcome = judgments(list, filed);
  // Judged since the list was read: what this screen filed that succeeded, and what the executor remembers of any judgment since —
  // so looking at an entity and coming back does not offer a judged pair again.
  const judged = new Set([...outcome.done, ...judgedSince(list)]);
  const left = listed ? awaiting(listed.pairs, judged) : [];
  const changeable = judgedPairs(list);

  const refresh = () => {
    setRefused(null);
    setFiled(new Map());
    api.startJob(group, "candidates").then((answer) => {
      if (!answer.ok) {
        setRefused(whyNot(answer, "The console refused."));
      }
      load();
    });
  };

  const setComparison = (key: string, value: Comparison | null) =>
    setCompared((before) => {
      const next = new Map(before);
      if (value === null) {
        next.delete(key);
      } else {
        next.set(key, value);
      }
      return next;
    });

  const side = (c: PairCandidate) => {
    const key = pairKey(c);
    if (compared.has(key)) {
      setComparison(key, null);
      return;
    }
    setComparison(key, "reading");
    void (async () => {
      let declared = catalog;
      if (declared === null) {
        const answer = await api.catalog();
        if (!answer.ok) {
          setComparison(key, { problem: whyNot(answer, "The catalog could not be read.") });
          return;
        }
        declared = answer.value;
        setCatalog(declared);
      }
      const [one, other] = [c.firstEntity, c.secondEntity];
      if (one === null || other === null) {
        setComparison(key, { problem: "One of the two is under no declared type, so its fields cannot be read." });
        return;
      }
      const [first, second] = await Promise.all([
        api.lookup({ start: one, path: [], read: fieldsOf(declared, one.type) }),
        api.lookup({ start: other, path: [], read: fieldsOf(declared, other.type) }),
      ]);
      if (!first.ok) {
        setComparison(key, { problem: whyNot(first, "The lookup was refused.") });
        return;
      }
      if (!second.ok) {
        setComparison(key, { problem: whyNot(second, "The lookup was refused.") });
        return;
      }
      const fields = [...new Set([...fieldsOf(declared, one.type), ...fieldsOf(declared, other.type)])];
      setComparison(key, { rows: compare(fields, sideOf(first.value, one), sideOf(second.value, other)) });
    })();
  };

  const judge = (c: Pick<PairCandidate, "identity" | "first" | "second">, same: boolean) => {
    setRefused(null);
    api.startJob(group, "judge", { identity: c.identity, first: c.first, second: c.second, same }).then((answer) => {
      if (answer.ok) {
        // Accepted is not filed: the pair stays listed, as filing, until its job has succeeded.
        setFiled((before) => new Map(before).set(pairKey(c), answer.value.id));
      } else {
        setRefused(whyNot(answer, "The console refused."));
      }
      load();
    });
  };

  return (
    <>
      {settled?.unreached && (
        <p className="muted" role="status">
          {settled.unreached} Showing what was last read; asking again.
        </p>
      )}
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
                    <Fragment key={pairKey(c)}>
                    <tr>
                      <td>{c.identity}</td>
                      <td><Entity iri={c.first} entity={c.firstEntity} onOpen={onOpenLocal} /></td>
                      <td><Entity iri={c.second} entity={c.secondEntity} onOpen={onOpenLocal} /></td>
                      <td className="muted">{c.score}</td>
                      <td>
                        <button className="button" disabled={running !== undefined} onClick={() => judge(c, true)}>
                          Same
                        </button>{" "}
                        <button className="button" disabled={running !== undefined} onClick={() => judge(c, false)}>
                          Different
                        </button>{" "}
                        <button className="link" title="Read both as yourself and lay their fields side by side" onClick={() => side(c)}>
                          {compared.has(pairKey(c)) ? "Hide" : "Compare"}
                        </button>
                        {outcome.pending.has(pairKey(c)) && <span className="muted"> Filing the judgment…</span>}
                        {outcome.failed.has(pairKey(c)) && (
                          <span className="refusal" role="alert">
                            {" "}
                            The judgment was not filed ({outcome.failed.get(pairKey(c))!.state}
                            {outcome.failed.get(pairKey(c))!.exitCode === null ? "" : `, exit ${outcome.failed.get(pairKey(c))!.exitCode}`}) — the pair still waits.
                          </span>
                        )}
                        {outcome.unknown.has(pairKey(c)) && <span className="muted"> The executor no longer remembers this judgment — read the pairs again.</span>}
                      </td>
                    </tr>
                    {compared.has(pairKey(c)) && (
                      <tr>
                        <td colSpan={5}>
                          <Side comparison={compared.get(pairKey(c))!} />
                        </td>
                      </tr>
                    )}
                    </Fragment>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          {changeable.length > 0 && (
            <>
              <h2>Judged since the pairs were read</h2>
              <p className="muted">A later judgment of a pair corrects the earlier one on the group's next pass. The executor remembers only its latest jobs, so an older judgment can no longer be changed from here.</p>
              <div className="graph">
                <table>
                  <thead>
                    <tr>
                      <th>Identity</th>
                      <th>One entity</th>
                      <th>The other</th>
                      <th>Judged</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {changeable.map((j) => (
                      <tr key={pairKey(j)}>
                        <td>{j.identity}</td>
                        <td><code>{j.first}</code></td>
                        <td><code>{j.second}</code></td>
                        <td>
                          {j.same ? "Same" : "Different"} <span className="muted">· {j.by}</span>
                        </td>
                        <td>
                          <button className="button" disabled={running !== undefined} onClick={() => judge(j, !j.same)}>
                            {j.same ? "Change to Different" : "Change to Same"}
                          </button>
                          {outcome.pending.has(pairKey(j)) && <span className="muted"> Filing the judgment…</span>}
                          {outcome.failed.has(pairKey(j)) && (
                            <span className="refusal" role="alert">
                              {" "}
                              The change was not filed ({outcome.failed.get(pairKey(j))!.state}) — the earlier judgment stands.
                            </span>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </>
          )}
        </>
      )}
    </>
  );
}

/** An entity by its IRI, with a way to see what the person judging can read of it: the local graph walks it as them. */
function Entity({ iri, entity, onOpen }: { iri: string; entity: EntityKey | null; onOpen: (entity: EntityKey) => void }) {
  return (
    <>
      <code>{iri}</code>
      {entity && (
        <>
          {" "}
          <button className="link" title="Read it as yourself in the local graph" onClick={() => onOpen(entity)}>
            Look
          </button>
        </>
      )}
    </>
  );
}

/** What comparing a pair came to: still reading, why it could not be read, or the fields side by side. */
type Comparison = "reading" | { problem: string } | { rows: CompareRow[] };

/** The two entities' declared fields in two columns, differences marked — what this person can read of each, no more. */
function Side({ comparison }: { comparison: Comparison }) {
  if (comparison === "reading") {
    return <p className="muted">Reading both as you…</p>;
  }
  if ("problem" in comparison) {
    return <p role="alert">{comparison.problem}</p>;
  }
  if (comparison.rows.length === 0) {
    return <p className="muted">Neither declares a field to read.</p>;
  }
  return (
    <table>
      <thead>
        <tr>
          <th>Field</th>
          <th>One entity</th>
          <th>The other</th>
          <th />
        </tr>
      </thead>
      <tbody>
        {comparison.rows.map((r) => (
          <tr key={r.field} className={r.verdict === "different" ? "differs" : undefined}>
            <th scope="row">{r.field}</th>
            <td>{r.one ?? <span className="muted">—</span>}</td>
            <td>{r.other ?? <span className="muted">—</span>}</td>
            <td className="muted">{r.verdict === "same" ? "same" : r.verdict === "different" ? "differs" : "one side only — empty, or not readable by you"}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
