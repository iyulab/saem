import type { Answer, Catalog, CheckReport, GroupStatus, Job, JobKind, PairCandidate } from "./api";

/** What a builder may ask a group's executor for from the screen, with what each does to the group's store. */
export const asks: ReadonlyArray<{ kind: JobKind; title: string; writes: boolean; says: string }> = [
  { kind: "verify", title: "Verify", writes: false, says: "Compare the store with what a pass would give now; writes nothing." },
  { kind: "status", title: "Status", writes: false, says: "Read what the store holds in counts and times: the last pass, each relation's edges, each identity's things." },
  { kind: "check", title: "Check", writes: false, says: "Check the declarations against the sources as the group's account." },
  { kind: "changed-pass", title: "Changed rows pass", writes: true, says: "Read only the rows changed since the last pass and correct the store." },
  { kind: "pass", title: "Whole pass", writes: true, says: "Read everything and make the store hold exactly what the sources give now." },
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
