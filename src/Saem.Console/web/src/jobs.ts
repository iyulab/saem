import type { Answer, Catalog, CheckReport, GroupStatus, Job, JobKind, PairCandidate } from "./api";

/**
 * What a builder may ask a group's executor for from the screen, with what each does to the group's copy (its store).
 * `label` is the button's words; `term` is what the executor's own documentation calls it, kept in small print beside it.
 */
export const asks: ReadonlyArray<{ kind: JobKind; title: string; label: string; term: string; writes: boolean; says: string }> = [
  { kind: "pass", title: "Whole pass", label: "Refresh from the source", term: "whole pass", writes: true, says: "Read everything the group's account may see and make the group's copy match it exactly." },
  { kind: "changed-pass", title: "Changed rows pass", label: "Only what changed", term: "changed-rows pass", writes: true, says: "Read only the rows the source changed since the last pass and correct the group's copy for them." },
  { kind: "verify", title: "Verify", label: "Verify the copy", term: "verify", writes: false, says: "Compare the group's copy with what a pass would give now; writes nothing." },
  { kind: "check", title: "Check", label: "Check the declarations", term: "check", writes: false, says: "Check the declarations against the sources as the group's account." },
  { kind: "status", title: "Status", label: "Read the counts", term: "status", writes: false, says: "Read what the group's copy holds in counts and times: the last pass, each relation's edges, each identity's things." },
];

export const isRunning = (job: Job) => job.state === "running";

/** How long a job ran, or has been running, in the words a person reads. */
export function took(job: Job, now: Date): string {
  const end = job.finishedAt ? new Date(job.finishedAt) : now;
  const seconds = Math.max(0, Math.round((end.getTime() - new Date(job.startedAt).getTime()) / 1000));
  if (seconds < 60) {
    return `${seconds} s`;
  }
  return `${Math.floor(seconds / 60)} min ${seconds % 60} s`;
}

/** The job in a line: what it was, how it ended, and who asked. */
export function summary(job: Job): string {
  const ending = job.state === "running" ? "running" : `${job.state}${job.exitCode === null ? "" : ` (exit ${job.exitCode})`}`;
  return `${job.kind} · ${ending} · ${job.requestedBy}`;
}

/** What a running job leaves behind when it is stopped part-way, as a question to put to the person — null for a job that writes nothing. */
export function cancelWarning(job: Job): string | null {
  const writes = job.kind === "fit" || job.kind === "judge" || asks.some((a) => a.kind === job.kind && a.writes);
  return writes
    ? `Stop the ${job.kind} ${job.requestedBy} started? It writes to the group's store, and stopping it part-way may leave the store half-corrected until the next pass.`
    : null;
}

/** The job running now, if any — an executor does one at a time, so a request while one runs is refused. */
export const runningJob = (jobs: readonly Job[]): Job | undefined => jobs.find(isRunning);

/** A check's report, when the job is a check that filed one. */
export const checkReport = (job: Job): CheckReport | null => (job.kind === "check" && job.report !== null && "findings" in job.report ? job.report : null);

/** The findings of a check's report that say a declaration does not hold. */
export const violations = (job: Job) => checkReport(job)?.findings.filter((f) => f.violation) ?? [];

/** What a finished status job read of the group's store. */
export const statusOf = (job: Job): GroupStatus | null =>
  job.kind === "status" && job.state === "succeeded" && job.report !== null && "status" in job.report ? job.report.status : null;

/** The pairs a finished candidates job listed, or null when the job is another kind, did not finish or listed nothing it could read. */
export const candidatesOf = (job: Job): PairCandidate[] | null =>
  job.kind === "candidates" && job.state === "succeeded" && job.report !== null && "candidates" in job.report ? job.report.candidates : null;

/** The newest candidates list an executor still remembers — its jobs come newest first — and the job that made it. */
export function latestCandidates(jobs: readonly Job[]): { job: Job; pairs: PairCandidate[] } | null {
  for (const job of jobs) {
    const pairs = candidatesOf(job);
    if (pairs !== null) {
      return { job, pairs };
    }
  }
  return null;
}

/** The pair as one key, whichever way round it is read. */
export const pairKey = (c: Pick<PairCandidate, "identity" | "first" | "second">) =>
  `${c.identity} ${c.first < c.second ? `${c.first} ${c.second}` : `${c.second} ${c.first}`}`;

/** What a person is yet to judge: the listed pairs less the ones judged since the list was made. */
export const awaiting = (pairs: readonly PairCandidate[], judged: ReadonlySet<string>): PairCandidate[] => pairs.filter((c) => !judged.has(pairKey(c)));

/**
 * What became of the judgments a person filed this session — each pair's key mapped to the job that carries it. A pair is judged
 * only when its job succeeded: a judgment is a job the executor takes up after it was accepted, and one that fails files nothing.
 * `unknown` is a job the executor no longer remembers (it keeps the latest few), whose outcome this screen cannot say.
 */
export function judgments(jobs: readonly Job[], filed: ReadonlyMap<string, string>) {
  const done = new Set<string>();
  const pending = new Set<string>();
  const unknown = new Set<string>();
  const failed = new Map<string, Job>();
  for (const [pair, id] of filed) {
    const job = jobs.find((j) => j.id === id);
    if (job === undefined) {
      unknown.add(pair);
    } else if (job.state === "succeeded") {
      done.add(pair);
    } else if (job.state === "running") {
      pending.add(pair);
    } else {
      failed.set(pair, job);
    }
  }
  return { done, pending, failed, unknown };
}

/** What a fit does, and what it writes. */
export const fitSays = "Fit the identity's linkage rule to the group's records and keep the fit in its store, for its passes to score with.";

/** The identities a fit may be asked for: those that name a linkage rule to fit. */
export const fitIdentities = (catalog: Catalog): string[] =>
  catalog.declarations.filter((d) => d.kind === "identity" && typeof d.linkage === "string" && d.linkage !== "").map((d) => d.name);

/** A pair a person judged, as the executor's job for it says. */
export type JudgedPair = { identity: string; first: string; second: string; same: boolean; by: string };

/**
 * The pairs judged since the newest list was read, from the jobs the executor remembers — not from what this screen filed, so a
 * judgment is not forgotten when the person looks at an entity and comes back, or someone else filed it. The jobs come newest first;
 * a pair the list was made after is already out of it. A pair judged more than once appears once, as its latest judgment says:
 * a later judgment of the same pair corrects the earlier one, so this is what a person may still change.
 */
export function judgedPairs(jobs: readonly Job[]): JudgedPair[] {
  const seen = new Set<string>();
  const pairs: JudgedPair[] = [];
  for (const job of jobs) {
    if (job.kind === "candidates" && candidatesOf(job) !== null) {
      break;
    }
    const { identity, first, second, same } = job.arguments;
    if (job.kind === "judge" && job.state === "succeeded" && identity && first && second) {
      const key = pairKey({ identity, first, second });
      if (!seen.has(key)) {
        seen.add(key);
        pairs.push({ identity, first, second, same: same === true, by: job.requestedBy });
      }
    }
  }
  return pairs;
}

/** The keys of the pairs judged since the newest list was read — what a person is no longer asked to judge. */
export const judgedSince = (jobs: readonly Job[]): Set<string> => new Set(judgedPairs(jobs).map(pairKey));

/** A screen's jobs as last read, and why they may be older than the executor's: the poll that came after got no answer. */
export type Settled = { jobs: Answer<Job[]>; unreached: string | null };

/**
 * What a screen shows after a read: the answer — except that a read that got no answer (the connection dropped, the console
 * failed) leaves what was last read in place and says so, so a screen that was waiting on a running job keeps waiting and asks again.
 */
export function settle(before: Settled | null, answer: Answer<Job[]>): Settled {
  const silent = !answer.ok && (answer.status === 0 || answer.status >= 500);
  return silent && before?.jobs.ok === true ? { jobs: before.jobs, unreached: "problem" in answer ? answer.problem : "The console gave no answer." } : { jobs: answer, unreached: null };
}


/** A time as a person says it: how long ago, to the unit that matters. */
export function ago(then: string | null | undefined, now: Date): string | null {
  if (!then) {
    return null;
  }
  const seconds = Math.max(0, Math.round((now.getTime() - new Date(then).getTime()) / 1000));
  if (seconds < 90) {
    return "just now";
  }
  if (seconds < 3600) {
    return `${Math.round(seconds / 60)} min ago`;
  }
  if (seconds < 86400) {
    return `${Math.round(seconds / 3600)} h ago`;
  }
  return `${Math.round(seconds / 86400)} d ago`;
}

/** What a finished job says in one line a person reads: the numbers a pass prints, in the order they are asked about. */
export type Outcome = { title: string; facts: string[]; tone: "ok" | "warn" | "bad"; took: string };

const passCounts = /(\d+) edge\(s\) written, (\d+) unchanged, (\d+) retired/;
const verifyCounts = /(\d+) edge\(s\) as the sources give them, (\d+) missing, (\d+) different, (\d+) no longer given/;

/** A job in plain words: which ask it was, and what it did — wrote · kept · retired for a pass, agreed or not for a verify. Null for a job still running. */
export function outcomeOf(job: Job, now: Date): Outcome | null {
  if (isRunning(job)) {
    return null;
  }
  const ask = asks.find((a) => a.kind === job.kind);
  const title = `${ask?.label ?? job.kind} · asked by ${job.requestedBy}`;
  const duration = took(job, now);
  const verifyFirst = job.kind === "verify" ? verifyCounts.exec(job.output) : null;
  if (job.state !== "succeeded" && !verifyFirst) {
    return { title, facts: [job.state === "cancelled" ? "Stopped before it finished" : `Failed (exit ${job.exitCode ?? "?"})`], tone: "bad", took: duration };
  }
  const pass = passCounts.exec(job.output);
  if (pass && (job.kind === "pass" || job.kind === "changed-pass")) {
    return { title, facts: [`wrote ${pass[1]}`, `kept ${pass[2]}`, `retired ${pass[3]}`], tone: "ok", took: duration };
  }
  const verify = verifyFirst;
  if (verify) {
    const differs = Number(verify[2]) + Number(verify[3]) + Number(verify[4]);
    return { title, facts: differs === 0 ? [`${verify[1]} edge${verify[1] === "1" ? "" : "s"}, all as the source gives them`] : [`${verify[2]} missing`, `${verify[3]} different`, `${verify[4]} no longer given`], tone: differs === 0 ? "ok" : "warn", took: duration };
  }
  const report = checkReport(job);
  if (report) {
    return { title, facts: [report.holds ? "every declaration holds" : `${violations(job).length} finding(s)`], tone: report.holds ? "ok" : "warn", took: duration };
  }
  return { title, facts: [], tone: "ok", took: duration };
}

/** The newest finished job of one of these kinds, from jobs that come newest first. */
export const latestOf = (jobs: readonly Job[], kinds: readonly JobKind[]): Job | undefined =>
  jobs.find((j) => kinds.includes(j.kind) && j.state === "succeeded");

/** The group's copy in a few facts, from the newest status job and the newest verify. */
export function copyFacts(jobs: readonly Job[]) {
  const status = latestOf(jobs, ["status"]);
  const report = status ? statusOf(status) : null;
  const refreshed = latestOf(jobs, ["pass", "changed-pass"]);
  const verify = jobs.find((j) => j.kind === "verify" && !isRunning(j) && j.exitCode !== null && j.finishedAt !== null);
  const pass = latestOf(jobs, ["pass", "changed-pass"]);
  const lastPass = pass ? passCounts.exec(pass.output) : null;
  return {
    lastPass: lastPass ? { kind: pass!.kind, wrote: Number(lastPass[1]), kept: Number(lastPass[2]), retired: Number(lastPass[3]), by: pass!.requestedBy } : null,
    asOf: [report?.asOf, refreshed?.finishedAt].filter((x): x is string => !!x).sort().pop() ?? null,
    edges: report ? report.relations.reduce((sum, r) => sum + r.edges, 0) : null,
    relations: report ? report.relations.length : null,
    agrees: verify ? verify.exitCode === 0 : null,
    verifiedAt: verify?.finishedAt ?? null,
  };
}

/**
 * Whether the counts a card shows are older than the group's last write — or were never read — so that reading them again is the
 * card's next step. A job's id is returned so one write is answered with one read, not a loop. Jobs come newest first.
 */
export function countsStale(jobs: readonly Job[]): string | null {
  const status = latestOf(jobs, ["status"]);
  const write = latestOf(jobs, ["pass", "changed-pass", "fit", "judge"]);
  if (!status) {
    return write?.id ?? "none";
  }
  return write && write.finishedAt && status.finishedAt && write.finishedAt > status.finishedAt ? write.id : null;
}
