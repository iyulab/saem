import type { Job, JobKind } from "./api";

/** What a builder may ask a group's executor for from the screen, with what each does to the group's store. */
export const asks: ReadonlyArray<{ kind: JobKind; title: string; writes: boolean; says: string }> = [
  { kind: "verify", title: "Verify", writes: false, says: "Compare the store with what a pass would give now; writes nothing." },
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

/** The findings of a check's report that say a declaration does not hold. */
export const violations = (job: Job) => job.report?.findings.filter((f) => f.violation) ?? [];
