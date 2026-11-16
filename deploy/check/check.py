#!/usr/bin/env python3
"""Checks an installed saem against its sample, from inside the compose network:

    docker compose -f deploy/compose.yaml run --rm check            # read-only after asking each group for a whole pass
    docker compose -f deploy/compose.yaml run --rm check --change   # also corrects and retires a filed row (once per install)

What it asks, as the people the sample names: the answer key as alice and the refusals for bob; the groups' partitions as
pat, quinn, olive and a person in no group; that no CANARY mark reaches anyone who may not read its row; that every cited
source is a filed document saying the value; that one group's confined secrets reach nothing of the other's; and, with
--change, that a corrected row reaches the answers and a changed-rows pass agrees with a whole one.

It needs the catalog in force to be the sample's (confirm the draft in the console first). Standard library only. Exits non-zero
on any FAIL; prints counts, never a key a person may not read.
"""
from __future__ import annotations

import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

CANARIES = ["CANARY-5e02-hex-bolt", "CANARY-8c3d-wo-restricted"]
GROUPS = ["g-plant", "g-office"]
ROWS: list[dict] = []
DATA = Path(os.environ.get("SAEM_DATA", "/data"))
SECRETS = Path(os.environ.get("SAEM_SECRETS", "/secrets"))
ISSUER = os.environ["SAEM_ISSUER_URL"]
MCP = os.environ.get("SAEM_MCP_URL", "http://mcp:8080")
FORMBASE = os.environ.get("SAEM_SOURCE_FORMBASE_URL", "http://formbase:8080")
FORMS = os.environ.get("SAEM_FORMS_URL", "http://forms:8080")


def row(check: str, ok: bool, detail: str) -> None:
    ROWS.append({"Check": check, "Result": "pass" if ok else "FAIL", "Detail": detail})


def http(method: str, url: str, headers: dict | None = None, body=None, timeout: int = 60):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, method=method, data=data, headers=headers or {})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, r.read().decode()
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()
    except urllib.error.URLError as e:  # a refused connection is not an answer
        return 0, str(e.reason)


def group_env(group: str) -> dict:
    out = {}
    for line in (SECRETS / f"{group}.env").read_text().splitlines():
        if line.startswith("export "):
            name, _, value = line[7:].partition("=")
            out[name] = value.strip("'")
    return out


class Install:
    def __init__(self) -> None:
        self.catalog = json.loads((DATA / "catalog.json").read_text())
        rel = next((c for c in self.catalog if c["kind"] == "relation" and c.get("inverse")), None)
        if not rel:
            raise SystemExit("The catalog in force names no relation with an `inverse` — confirm the sample draft in the console first.")
        self.relation, self.inverse = rel["name"], rel["inverse"]
        types = {c["name"]: c for c in self.catalog if c["kind"] == "entityType"}
        self.wo, self.item = types[rel["from"]], types[rel["to"]]
        self.documents = json.loads((DATA / "seed" / "documents.json").read_text())
        self.doc_ids = set(self.documents.values())
        self.groups = {}
        for g in GROUPS:
            env, tag = group_env(g), g.upper().replace("-", "_")
            self.groups[g] = {
                "executor": f"http://executor-{g}:8080", "executor_secret": os.environ[f"SAEM_{tag}_EXECUTOR_SECRET"],
                "store": f"http://formbase-{g}:8080", "access_key": os.environ[f"SAEM_{tag}_ACCESS_KEY"],
                "store_project": os.environ[f"SAEM_{tag}_PROJECT_ID"],
                "store_secret": env["Formbase__MorphDb__Secret"], "source_secret": env["SAEM_SOURCE_SECRET_FORMS"],
            }

    def field(self, type_: dict, column: str) -> str:
        return next(k for k, v in type_["fields"].items() if v == column)

    def token(self, who: str) -> str:
        body = urllib.parse.urlencode({"grant_type": "client_credentials", "client_id": who, "client_secret": "unused", "scope": "saem-rig", "audience": "saem"}).encode()
        with urllib.request.urlopen(urllib.request.Request(ISSUER + "/token", data=body)) as r:
            return json.load(r)["access_token"]

    def call(self, who: str, arguments: dict) -> tuple[dict, str]:
        """One MCP lookup as a person: (structured answer or error, raw text — everything the person received)."""
        status, text = http("POST", MCP + "/mcp",
                            {"Authorization": "Bearer " + self.token(who), "Content-Type": "application/json", "Accept": "application/json, text/event-stream"},
                            {"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": "lookup", "arguments": arguments}})
        data = [l[5:].strip() for l in text.splitlines() if l.startswith("data:")]
        result = json.loads(data[-1] if data else text).get("result", {})
        if result.get("isError"):
            return {"kind": "error", "text": " ".join(c.get("text", "") for c in result.get("content", []))}, text
        return result.get("structuredContent") or {"kind": "error", "text": json.dumps(result)[:200]}, text

    def lookup(self, who, start_type, key, path, read):
        return self.call(who, {"start": {"type": start_type, "key": key}, "path": path, "read": [read]})

    def reverse(self, who: str, item_key: str, read: str = "qty"):
        return self.lookup(who, self.item["name"], item_key, [self.inverse], self.field(self.wo, read))

    def job(self, group: str, kind: str) -> dict:
        g = self.groups[group]
        h = {"Authorization": "Bearer " + g["executor_secret"], "Content-Type": "application/json"}
        _, text = http("POST", g["executor"] + "/jobs", h, {"kind": kind, "requestedBy": "pat"})
        job = json.loads(text)
        for _ in range(180):
            if "id" not in job:
                break
            time.sleep(1)
            _, text = http("GET", g["executor"] + "/jobs/" + job["id"], h)
            job = json.loads(text)
            if job["state"] not in ("queued", "running"):
                break
        return job

    def document(self, doc_id: str):
        status, text = http("GET", f"{FORMBASE}/documents/{doc_id}")
        return json.loads(text) if status == 200 else None


def claims_of(answer: dict) -> dict:
    return {c["entity"]["key"]: c["value"] for c in answer.get("claims", [])}


def citations(inst: Install, answers: list[dict]) -> tuple[int, int]:
    """(citations read, citations that are not a filed document of the cited table saying the claimed value)."""
    total = bad = 0
    for a in answers:
        for claim in a.get("claims", []):
            srcs = claim.get("sources", [])
            if not srcs:
                total += 1
                bad += 1
            for src in srcs:
                total += 1
                doc = inst.document(src["record"]) if src["record"] in inst.doc_ids else None
                if not doc or doc["formType"] != src["table"]:
                    bad += 1
                elif claim["entity"]["key"] not in json.dumps(doc["body"]) or str(claim["value"]) not in {str(v) for v in doc["body"].values()}:
                    bad += 1
            for hop in claim.get("path", []):
                src = hop.get("source")
                if src:
                    total += 1
                    bad += src["record"] not in inst.doc_ids
    return total, bad


def first_line(job: dict) -> str:
    return (job.get("output") or "").splitlines()[0] if job.get("output") else str(job.get("state"))


def main() -> None:
    inst = Install()
    received: dict[str, list[str]] = {w: [] for w in ("alice", "bob", "pat", "quinn", "olive", "nobody")}
    answered: list[dict] = []

    def keep(who: str, answer: dict, raw: str) -> dict:
        received[who].append(raw)
        answered.append(answer)
        return answer

    # The groups' copies: a whole pass for each, so the partitions answer (a pass is safe to repeat).
    for g in GROUPS:
        j = inst.job(g, "pass")
        row(f"pass {g}", j.get("state") == "succeeded", first_line(j))

    # -- First half: the answer key as alice, the refusals for bob, undeclared asks -------------------------------------------
    wo, item, rel = inst.wo["name"], inst.item["name"], inst.relation
    name_f, owner_f, qty_f = inst.field(inst.item, "item_name"), inst.field(inst.item, "owner"), inst.field(inst.wo, "qty")
    key1 = [("Q1", wo, "WO-2401", True, name_f, "CANARY-5e02-hex-bolt"), ("Q2", wo, "WO-2402", True, name_f, "Ball bearing 6204"),
            ("Q3", wo, "WO-2403", True, name_f, "Hex nut M8"), ("Q4", wo, "WO-2404", True, name_f, "Ball bearing 6204"),
            ("Q5", wo, "WO-2401", False, qty_f, "500"), ("Q6", wo, "WO-2402", False, qty_f, "40"),
            ("Q7", item, "I-200", False, name_f, "Hex nut M8"), ("Q8", item, "I-300", False, owner_f, "alice")]
    correct = 0
    for qid, t, k, hop, read, expect in key1:
        a, raw = inst.lookup("alice", t, k, [rel] if hop else [], read)
        keep("alice", a, raw)
        got = str(a["claims"][0]["value"]) if a.get("kind") == "answered" and a.get("claims") else f"<{a.get('kind')}>"
        correct += got == expect
        row(f"first-half {qid}", got == expect, f"{k} -> {got}")
    row("first-half answer key", correct >= 7, f"{correct} / 8 (threshold >= 7)")

    bob = [("bob WO-2401 -> item_name", wo, "WO-2401", [rel], name_f, "refused"), ("bob WO-2402 -> item_name", wo, "WO-2402", [rel], name_f, "refused"),
           ("bob I-100 directly", item, "I-100", [], name_f, "refused"), ("bob's own WO-2501 qty", wo, "WO-2501", [], qty_f, "answered"),
           ("bob's own WO-2502 -> item_name", wo, "WO-2502", [rel], name_f, "answered")]
    for label, t, k, path, read, expect in bob:
        a, raw = inst.lookup("bob", t, k, path, read)
        keep("bob", a, raw)
        row(f"first-half {label}", a.get("kind") == expect, f"{a.get('kind')} (expected {expect})")
    for label, t, k, path, read in [("undeclared type", "suppliers", "S-1", [], "name"), ("undeclared relation", wo, "WO-2401", ["supplier"], name_f),
                                    ("undeclared field", item, "I-100", [], "price")]:
        a, _ = inst.lookup("alice", t, k, path, read)
        row(f"first-half {label}", a.get("kind") == "error" and ("not a declared" in a.get("text", "") or "not a field" in a.get("text", "")), a.get("text", a.get("kind", ""))[:100])
    same = sum(1 for i in inst.doc_ids if (DATA / "seed" / "pre" / f"{i}.json").read_text() == http("GET", f"{FORMBASE}/documents/{i}")[1])
    row("first-half originals unchanged", same == len(inst.doc_ids), f"{same} of {len(inst.doc_ids)} documents identical to the snapshot taken when the sample was filed")

    # -- Second half: the groups' partitions, as the people the sample names -------------------------------------------------
    key2 = [("Q1", "pat", "I-300", {"WO-2402": 40, "WO-2404": 12, "WO-2701": 25, "CANARY-8c3d-wo-restricted": 777}, None),
            ("Q2", "olive", "I-300", {"WO-2701": 25}, None), ("Q3", "quinn", "I-300", {"WO-2402": 40, "WO-2404": 12, "WO-2701": 25}, None),
            ("Q4", "nobody", "I-300", None, "noMaterializedPartition"), ("Q6", "pat", "I-500", {"WO-2702": 60}, None),
            ("Q7", "olive", "I-500", {"WO-2702": 60}, None), ("Q8", "quinn", "I-100", None, "noVisibleResult")]
    correct, seen = 0, {}
    for qid, who, k, expect, reason in key2:
        a, raw = inst.reverse(who, k)
        seen[qid] = keep(who, a, raw)
        ok = (a.get("kind") == "answered" and claims_of(a) == expect) if expect is not None else (a.get("kind") == "refused" and (a.get("refusal") or {}).get("reason") == reason)
        correct += ok
        row(f"groups {qid}", ok, f"{who} {k} -> {a.get('kind')} {sorted(claims_of(a).items()) if a.get('claims') else (a.get('refusal') or {}).get('reason', '')}")
    fwd, raw = inst.lookup("nobody", wo, "WO-3001", [rel], name_f)
    keep("nobody", fwd, raw)
    ok = fwd.get("kind") == "answered" and [c["value"] for c in fwd["claims"]] == ["Spacer M5"]
    correct += ok
    row("groups Q5", ok, f"nobody WO-3001 -> {[c['value'] for c in fwd.get('claims', [])]} (expected ['Spacer M5'])")
    row("groups answer key", correct >= 7, f"{correct} / 8 (threshold >= 7)")

    hops = [h for q in ("Q1", "Q2") for c in seen[q].get("claims", []) for h in c.get("path", [])]
    row("groups partitions named", bool(hops) and all(h.get("partition") and h.get("partitionAsOf") for h in hops), f"{len(hops)} hops, each names its partition and the time its edges were read")
    row("groups two answers", claims_of(seen["Q1"]) != claims_of(seen["Q2"]) and {h["partition"] for c in seen["Q1"]["claims"] for h in c["path"]} == {"g-plant"}
        and {h["partition"] for c in seen["Q2"]["claims"] for h in c["path"]} == {"g-office"}, "pat is answered from g-plant's partition, olive from g-office's")
    parts = seen["Q3"].get("partitions", [])
    row("groups blocked bit", len(parts) == 1 and parts[0].get("name") == "g-plant" and parts[0].get("blocked") is True, f"quinn's partition report: {[(p.get('name'), 'blocked' if p.get('blocked') else 'open') for p in parts]}")
    row("groups one bit only", bool(parts) and set(parts[0]) <= {"name", "asOf", "blocked", "incomplete", "statements"}, "the partition says its name, read time, blocked and incomplete — no key, no count")
    row("groups pat reads the canary row", "CANARY-8c3d-wo-restricted" in claims_of(seen["Q1"]), "the same query as pat reaches the row quinn is not shown")

    # -- The canaries, the citations, the confinement ----------------------------------------------------------------------
    a, raw = inst.reverse("bob", "I-300")
    keep("bob", a, raw)
    leaks = sum(raw.count(c) for who in ("bob", "quinn", "nobody") for raw in received[who] for c in CANARIES)
    outputs = "".join(http("GET", g["executor"] + "/jobs", {"Authorization": "Bearer " + g["executor_secret"]})[1] for g in inst.groups.values())
    job_leaks = sum(outputs.count(c) for c in CANARIES)
    row("no canary mark", leaks + job_leaks == 0, f"canary marks in what bob · quinn · nobody received: {leaks}; in the executors' job output: {job_leaks} (threshold = 0)")
    total, bad = citations(inst, [x for x in answered if x.get("kind") == "answered"])
    row("every citation is a filed document", bad == 0 and total > 0, f"{total} citations, {bad} unsupplied or not the filed document saying the value (threshold = 0)")
    leaked, names = 0, list(inst.groups.items())
    for (an, a_), (bn, b_) in [(names[0], names[1]), (names[1], names[0])]:
        results = []
        for label, secret in (("its store's secret", a_["store_secret"]), ("its source account's secret", a_["source_secret"])):
            status, _ = http("GET", FORMS + "/api/schema/tables", {"Authorization": "Bearer " + secret, "X-Project-Id": b_["store_project"]})
            results.append((label, status))
            leaked += status == 200
        fb_status, _ = http("POST", b_["store"] + "/formtypes/x/documents", {"Authorization": "Bearer " + a_["access_key"], "Content-Type": "application/json"}, {})
        results.append(("its Formbase access key at the other Formbase", fb_status))
        leaked += fb_status in (200, 201)
        row(f"confined {an} -> {bn}", all(s in (401, 403) for _, s in results), "; ".join(f"{l}: {s}" for l, s in results))
    row("no reach across groups", leaked == 0, f"successful reaches across groups: {leaked} (threshold = 0)")

    if "--change" in sys.argv:
        change(inst)

    width = max(len(r["Check"]) for r in ROWS)
    for r in ROWS:
        print(f"{r['Check']:<{width}}  {r['Result']:<4}  {r['Detail']}")
    failed = [r for r in ROWS if r["Result"] == "FAIL"]
    print(f"\ncheck: {len(failed)} FAIL" if failed else "\ncheck: all pass")
    sys.exit(1 if failed else 0)


def change(inst: Install) -> None:
    """A record corrected and another retired (both appends), then the changed-rows pass and the whole pass."""
    before = {i: http("GET", f"{FORMBASE}/documents/{i}")[1] for i in inst.doc_ids}
    http("POST", f"{FORMBASE}/formtypes/work_orders/documents?recordKey=WO-2701", {"Content-Type": "application/json"}, {"wo_no": "WO-2701", "item_code": "I-500", "qty": 25, "owner": "olive"})
    http("POST", f"{FORMBASE}/formtypes/work_orders/projection")
    differ = 0
    for g in GROUPS:
        j = inst.job(g, "changed-pass")
        row(f"change changed-pass {g}", j["state"] == "succeeded", first_line(j))
        differ += inst.job(g, "verify")["exitCode"] != 0
    row("change incremental = whole", differ == 0, f"groups whose copy after the changed-rows pass differs from what a whole pass gives: {differ} (threshold = 0)")
    http("DELETE", f"{FORMBASE}/formtypes/work_orders/records?recordKey=WO-2702")
    http("POST", f"{FORMBASE}/formtypes/work_orders/projection")
    for g in GROUPS:
        inst.job(g, "changed-pass")
    p300, _ = inst.reverse("pat", "I-300")
    p500, _ = inst.reverse("pat", "I-500")
    row("change correction reaches the answer", "WO-2701" not in claims_of(p300) and claims_of(p500).get("WO-2701") == 25, f"pat I-300 {sorted(claims_of(p300))} · I-500 {sorted(claims_of(p500))}")
    row("change retired edge never answers", "WO-2702" not in claims_of(p500), f"I-500 answers {sorted(claims_of(p500))} (a stale edge may stay until a whole pass)")
    for g in GROUPS:
        inst.job(g, "pass")
        v = inst.job(g, "verify")
        row(f"change whole pass then verify {g}", v["exitCode"] == 0, first_line(v))
    p500, _ = inst.reverse("pat", "I-500")
    row("change after the whole pass", "WO-2702" not in claims_of(p500), f"I-500 answers {sorted(claims_of(p500))}")
    same = sum(1 for i, was in before.items() if http("GET", f"{FORMBASE}/documents/{i}")[1] == was)
    row("change originals unchanged", same == len(before), f"{same} of {len(before)} documents filed before the change are byte-identical now")


if __name__ == "__main__":
    main()
