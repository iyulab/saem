import { describe, expect, it } from "vitest";
import type { Answer, Catalog, Job } from "./api";
import { awaiting, cancelWarning, fitIdentities, judgedPairs, judgedSince, judgments, settle, statusOf, latestCandidates, pairKey, runningJob, summary, took, violations } from "./jobs";

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

  it("offers a fit for the identities that name a linkage rule, and no other", () => {
    const catalog: Catalog = {
      version: "v1",
      declarations: [
        { kind: "identity", name: "same_supplier", first: "Supplier", second: "Vendor", linkage: "names" },
        { kind: "identity", name: "same_order", first: "WorkOrder", second: "WorkOrder", match: [] },
        { kind: "linkage", name: "names" },
      ],
    };
    expect(fitIdentities(catalog)).toEqual(["same_supplier"]);
  });
});

describe("the pairs a person is yet to judge", () => {
  const pair = (first: string, second: string, score = 1) => ({ identity: "same_order", first, second, firstEntity: null, secondEntity: null, rule: "match", score });
  const listing = (pairs: ReturnType<typeof pair>[], more: Partial<Job> = {}) => job({ kind: "candidates", report: { candidates: pairs }, ...more });

  it("takes the newest finished listing, not one that failed or another kind of job", () => {
    const newest = listing([pair("a", "b")], { id: "n" });
    expect(latestCandidates([job({ kind: "judge" }), listing([pair("x", "y")], { state: "failed" }), newest, listing([pair("c", "d")])])?.job.id).toBe("n");
    expect(latestCandidates([job({}), job({ kind: "check" })])).toBeNull();
  });

  it("keeps a pair as one key whichever way round it is read", () => {
    expect(pairKey(pair("b", "a"))).toBe(pairKey(pair("a", "b")));
    expect(pairKey({ ...pair("a", "b"), identity: "other" })).not.toBe(pairKey(pair("a", "b")));
  });

  it("leaves out the pairs judged since the listing", () => {
    const pairs = [pair("a", "b", 9), pair("a", "c", 4)];
    expect(awaiting(pairs, new Set([pairKey(pair("b", "a"))])).map((c) => c.second)).toEqual(["c"]);
  });
});

describe("what a group's store holds", () => {
  const status = { asOf: "2026-10-25T07:00:00Z", relations: [], identities: [] };

  it("reads a finished status job, and no other", () => {
    expect(statusOf(job({ kind: "status", report: { status } }))).toBe(status);
    expect(statusOf(job({ kind: "status", state: "failed", report: { status } }))).toBeNull();
    expect(statusOf(job({ kind: "check", report: { status } }))).toBeNull();
  });
});

describe("what became of the judgments a person filed", () => {
  const filed = new Map([
    ["pair-a", "ja"],
    ["pair-b", "jb"],
    ["pair-c", "jc"],
    ["pair-d", "jd"],
  ]);

  it("counts a pair as judged only when its job succeeded", () => {
    const jobs = [
      job({ id: "ja", kind: "judge", state: "succeeded" }),
      job({ id: "jb", kind: "judge", state: "failed", exitCode: 2 }),
      job({ id: "jc", kind: "judge", state: "running", exitCode: null, finishedAt: null }),
    ];

    const result = judgments(jobs, filed);

    expect([...result.done]).toEqual(["pair-a"]);
    expect(result.failed.get("pair-b")?.exitCode).toBe(2);
    expect([...result.pending]).toEqual(["pair-c"]);
  });

  it("says a judgment whose job the executor no longer remembers is unknown, not done", () => {
    const result = judgments([job({ id: "ja", kind: "judge" })], filed);

    expect([...result.unknown].sort()).toEqual(["pair-b", "pair-c", "pair-d"]);
    expect(result.done.has("pair-d")).toBe(false);
  });

  it("counts a cancelled judgment as not done", () => {
    const result = judgments([job({ id: "ja", kind: "judge", state: "cancelled" })], new Map([["pair-a", "ja"]]));

    expect(result.done.size).toBe(0);
    expect(result.failed.get("pair-a")?.state).toBe("cancelled");
  });
});

describe("the pairs judged since the list was read, as the executor remembers them", () => {
  const judge = (id: string, first: string, second: string, state: Job["state"] = "succeeded") =>
    job({ id, kind: "judge", state, arguments: { identity: "same_order", first, second, same: true } });
  const listing = job({ id: "list", kind: "candidates", report: { candidates: [] } });

  it("counts the judgments that succeeded after the newest list, whoever filed them and whichever screen is open", () => {
    // Newest first, as the executor gives them: two judgments, then the list they came after, then one the list already took in.
    const done = judgedSince([judge("j2", "b", "a"), judge("j3", "c", "d", "failed"), judge("j1", "x", "y"), listing, judge("j0", "p", "q")]);

    expect([...done]).toEqual([pairKey({ identity: "same_order", first: "a", second: "b" }), pairKey({ identity: "same_order", first: "x", second: "y" })]);
  });

  it("counts none when the newest job is the list itself", () => {
    expect(judgedSince([listing, judge("j0", "p", "q")]).size).toBe(0);
  });
});

describe("the judgments a person may still change", () => {
  const judge = (id: string, first: string, second: string, same: boolean, by = "alice", state: Job["state"] = "succeeded") =>
    job({ id, kind: "judge", state, requestedBy: by, arguments: { identity: "same_order", first, second, same } });
  const listing = job({ id: "list", kind: "candidates", report: { candidates: [] } });

  it("lists each judged pair once with what was judged and by whom, the executor's latest judgment of it standing", () => {
    // Newest first: bob turns the pair a/b round after alice judged it the same; carol judged x/y different.
    const pairs = judgedPairs([judge("j3", "b", "a", false, "bob"), judge("j2", "x", "y", false, "carol"), judge("j1", "a", "b", true), listing, judge("j0", "p", "q", true)]);

    expect(pairs).toEqual([
      { identity: "same_order", first: "b", second: "a", same: false, by: "bob" },
      { identity: "same_order", first: "x", second: "y", same: false, by: "carol" },
    ]);
  });

  it("leaves out a judgment that did not succeed, and says nothing when no judgment came after the list", () => {
    expect(judgedPairs([judge("j2", "a", "b", true, "alice", "failed"), judge("j1", "c", "d", true, "alice", "running"), listing])).toEqual([]);
    expect(judgedPairs([listing, judge("j0", "p", "q", true)])).toEqual([]);
  });
});

describe("a poll that got no answer", () => {
  const ok: Answer<Job[]> = { ok: true, value: [job({ id: "a", state: "running", exitCode: null, finishedAt: null })] };

  it("keeps what was last read and says it could not be reached, so the poll goes on", () => {
    const before = settle(null, ok);
    const after = settle(before, { ok: false, status: 0, problem: "The console could not be reached." });

    expect(after.jobs).toBe(ok);
    expect(after.unreached).toMatch(/could not be reached/);
    expect(settle(after, ok).unreached).toBeNull();
  });

  it("shows a session that ended or a refusal as it is, and a first read that failed as failed", () => {
    expect(settle(settle(null, ok), { ok: false, status: 401 }).jobs).toEqual({ ok: false, status: 401 });
    expect(settle(settle(null, ok), { ok: false, status: 409, problem: "Busy." }).jobs).toEqual({ ok: false, status: 409, problem: "Busy." });
    expect(settle(null, { ok: false, status: 0, problem: "down" }).jobs).toEqual({ ok: false, status: 0, problem: "down" });
  });
});

describe("cancelling a running job", () => {
  const running = (kind: Job["kind"], requestedBy = "alice") => job({ kind, requestedBy, state: "running", exitCode: null, finishedAt: null });

  it("asks first when the job writes to the group's store, naming who started it", () => {
    for (const kind of ["pass", "changed-pass", "fit", "judge"] as const) {
      expect(cancelWarning(running(kind, "bob"))).toMatch(new RegExp(`${kind}.*bob`));
    }
  });

  it("does not ask when the job writes nothing", () => {
    for (const kind of ["verify", "status", "check", "candidates"] as const) {
      expect(cancelWarning(running(kind))).toBeNull();
    }
  });
});

