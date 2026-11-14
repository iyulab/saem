import { describe, expect, it } from "vitest";
import type { Answer, Catalog, Job } from "./api";
import { ago, awaiting, cancelWarning, copyFacts, countsStale, outcomeOf, fitIdentities, judgedPairs, judgedSince, judgments, settle, statusOf, latestCandidates, pairKey, runningJob, summary, took, violations } from "./jobs";

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


describe("a job in the words a person reads", () => {
  const now = new Date("2026-11-13T10:00:00Z");
  const done = (more: Partial<Job>) => job({ state: "succeeded", exitCode: 0, startedAt: "2026-11-13T09:59:57Z", finishedAt: "2026-11-13T10:00:00Z", requestedBy: "pat", ...more });

  it("says a pass's numbers as wrote, kept and retired, with who asked and how long it took", () => {
    const outcome = outcomeOf(done({ kind: "pass", output: "group g-plant: 10 edge(s) written, 2 unchanged, 1 retired; as of 2026-11-13T09:59:58Z.\n" }), now);

    expect(outcome).toEqual({ title: "Refresh from the source · asked by pat", facts: ["wrote 10", "kept 2", "retired 1"], tone: "ok", took: "3 s" });
  });

  it("says whether a verify found the copy agreeing with the source, and in what way it does not", () => {
    const agrees = outcomeOf(done({ kind: "verify", output: "group g: 9 edge(s) as the sources give them, 0 missing, 0 different, 0 no longer given, 0 document(s) skipped" }), now);
    const differs = outcomeOf(done({ kind: "verify", exitCode: 1, output: "group g: 9 edge(s) as the sources give them, 2 missing, 0 different, 1 no longer given, 0 document(s) skipped" }), now);

    expect(agrees).toMatchObject({ facts: ["matches the source (9 links)"], tone: "ok" });
    expect(differs).toMatchObject({ facts: ["2 missing", "0 different", "1 no longer given"], tone: "warn" });
    const failedVerify = outcomeOf(done({ kind: "verify", state: "failed", exitCode: 1, output: "group g: 9 edge(s) as the sources give them, 2 missing, 0 different, 1 no longer given, 0 document(s) skipped" }), now);
    expect(failedVerify).toMatchObject({ facts: ["2 missing", "0 different", "1 no longer given"], tone: "warn" });
    expect(copyFacts([job({ kind: "verify", state: "failed", exitCode: 1, finishedAt: "2026-11-13T10:00:00Z" })]).agrees).toBe(false);
  });

  it("says a failed or stopped job plainly and has nothing to say of one still running", () => {
    expect(outcomeOf(done({ kind: "pass", state: "failed", exitCode: 2 }), now)).toMatchObject({ facts: ["Failed (exit 2)"], tone: "bad" });
    expect(outcomeOf(done({ kind: "pass", state: "cancelled", exitCode: null }), now)).toMatchObject({ facts: ["Stopped before it finished"], tone: "bad" });
    expect(outcomeOf(job({ kind: "pass", state: "running", exitCode: null, finishedAt: null }), now)).toBeNull();
  });

  it("says why a failed job failed when its output ends in a line saying so, and keeps that line short", () => {
    const why = "The source's table 'work_orders' has two rows with the key 'WO-1'.";
    expect(outcomeOf(done({ kind: "pass", state: "failed", exitCode: 1, output: `reading work_orders\n${why}\n\n` }), now)).toMatchObject({ facts: ["Failed (exit 1)", why], tone: "bad" });
    expect(outcomeOf(done({ kind: "pass", state: "failed", exitCode: 1, output: "x".repeat(400) }), now)?.facts[1]).toHaveLength(161);
    expect(outcomeOf(done({ kind: "pass", state: "failed", exitCode: 1, output: "  \n" }), now)?.facts).toEqual(["Failed (exit 1)"]);
  });

  it("does not let a verify's verdict stand for a copy that a later write has changed", () => {
    const verify = done({ kind: "verify", exitCode: 0, startedAt: "2026-11-13T09:00:00Z", finishedAt: "2026-11-13T09:00:05Z" });
    const later = done({ kind: "pass", startedAt: "2026-11-13T09:30:00Z", finishedAt: "2026-11-13T09:30:10Z", output: "group g: 1 edge(s) written, 0 unchanged, 0 retired" });
    const earlier = done({ kind: "pass", startedAt: "2026-11-13T08:30:00Z", finishedAt: "2026-11-13T08:30:10Z", output: "group g: 1 edge(s) written, 0 unchanged, 0 retired" });
    const failedLater = done({ kind: "changed-pass", state: "failed", exitCode: 1, startedAt: "2026-11-13T09:40:00Z", finishedAt: "2026-11-13T09:40:03Z" });

    expect(copyFacts([later, verify]).agrees).toBeNull();
    expect(copyFacts([failedLater, verify]).agrees).toBeNull();
    expect(copyFacts([verify, earlier]).agrees).toBe(true);
    expect(copyFacts([later, verify]).verifiedAt).toBe("2026-11-13T09:00:05Z");
  });

  it("reads how long ago in the unit that matters", () => {
    expect(ago("2026-11-13T09:59:30Z", now)).toBe("just now");
    expect(ago("2026-11-13T09:40:00Z", now)).toBe("20 min ago");
    expect(ago("2026-11-13T07:00:00Z", now)).toBe("3 h ago");
    expect(ago("2026-11-11T10:00:00Z", now)).toBe("2 d ago");
    expect(ago(null, now)).toBeNull();
  });

  it("gathers a copy's facts from the newest status, pass and verify", () => {
    const status = done({ kind: "status", report: { status: { asOf: "2026-11-13T09:50:00Z", relations: [{ relation: "item", edges: 7, watermark: null }, { relation: "x", edges: 2, watermark: null }], identities: [] } } });
    const verify = done({ kind: "verify", exitCode: 0 });

    const pass = done({ kind: "changed-pass", requestedBy: "olive", output: "group g: 3 edge(s) written, 6 unchanged, 1 retired (1 relation(s) read where changed, 4 row(s) read)" });

    expect(copyFacts([verify, status])).toMatchObject({ asOf: "2026-11-13T09:50:00Z", edges: 9, relations: 2, agrees: true, lastPass: null });
    expect(copyFacts([pass, status]).lastPass).toEqual({ kind: "changed-pass", wrote: 3, kept: 6, retired: 1, by: "olive" });
    expect(copyFacts([])).toEqual({ lastPass: null, asOf: null, edges: null, relations: null, agrees: null, verifiedAt: null });
  });
});

describe("when a card reads a group's counts again", () => {
  const at = (finishedAt: string, more: Partial<Job>) => job({ finishedAt, startedAt: finishedAt, ...more });

  it("reads them when none were read, once for each write that came after the last read", () => {
    const status = at("2026-11-13T10:00:00Z", { id: "s", kind: "status" });
    const pass = at("2026-11-13T10:05:00Z", { id: "p", kind: "pass" });

    expect(countsStale([])).toBe("none");
    expect(countsStale([pass])).toBe("p");
    expect(countsStale([status])).toBeNull();
    expect(countsStale([pass, status])).toBe("p");
    expect(countsStale([at("2026-11-13T10:06:00Z", { id: "s2", kind: "status" }), pass, status])).toBeNull();
  });
});
