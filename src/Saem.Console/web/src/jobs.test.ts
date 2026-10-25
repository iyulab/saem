import { describe, expect, it } from "vitest";
import type { Job } from "./api";
import { runningJob, summary, took, violations } from "./jobs";

const job = (more: Partial<Job>): Job => ({
  id: "0123456789abcdef0123456789abcdef",
  kind: "pass",
  requestedBy: "alice",
  arguments: {},
  state: "succeeded",
  exitCode: 0,
  output: "",
  outputTruncated: false,
  startedAt: "2026-10-25T07:00:00Z",
  finishedAt: "2026-10-25T07:02:05Z",
  report: null,
  ...more,
});

describe("a group's jobs", () => {
  it("says how long a job took, or has been running", () => {
    expect(took(job({}), new Date("2026-10-25T09:00:00Z"))).toBe("2 min 5 s");
    expect(took(job({ finishedAt: "2026-10-25T07:00:09Z" }), new Date())).toBe("9 s");
    expect(took(job({ state: "running", finishedAt: null }), new Date("2026-10-25T07:00:30Z"))).toBe("30 s");
  });

  it("puts a job in a line with how it ended and who asked", () => {
    expect(summary(job({}))).toBe("pass · succeeded (exit 0) · alice");
    expect(summary(job({ state: "failed", exitCode: 2, kind: "check" }))).toBe("check · failed (exit 2) · alice");
    expect(summary(job({ state: "running", exitCode: null, finishedAt: null }))).toBe("pass · running · alice");
  });

  it("finds the job in the way", () => {
    const running = job({ id: "b", state: "running", exitCode: null, finishedAt: null });
    expect(runningJob([job({}), running])).toBe(running);
    expect(runningJob([job({})])).toBeUndefined();
  });

  it("keeps the findings that say a declaration does not hold", () => {
    const report = {
      checkedAt: "t",
      catalogVersion: "v1",
      holds: false,
      findings: [
        { type: "Item", relation: null, kind: "read", violation: false, what: "1 row(s) read", count: 1, recordsCited: 0 },
        { type: "Item", relation: null, kind: "key", violation: true, what: "2 rows share a key", count: 2, recordsCited: 2 },
      ],
    };
    expect(violations(job({ kind: "check", report })).map((f) => f.kind)).toEqual(["key"]);
    expect(violations(job({}))).toEqual([]);
  });
});
