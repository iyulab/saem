import type { Catalog, CheckReport, GroupStatus, Job, JobKind, PairCandidate } from "./api";

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

/** What a fit does, and what it writes. */
export const fitSays = "Fit the identity's linkage rule to the group's records and keep the fit in its store, for its passes to score with.";

/** The identities a fit may be asked for: those that name a linkage rule to fit. */
export const fitIdentities = (catalog: Catalog): string[] =>
  catalog.declarations.filter((d) => d.kind === "identity" && typeof d.linkage === "string" && d.linkage !== "").map((d) => d.name);
