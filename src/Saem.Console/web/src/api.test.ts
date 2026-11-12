import { afterEach, describe, expect, it, vi } from "vitest";
import { api } from "./api";

afterEach(() => vi.unstubAllGlobals());

const answering = (response: Response | Error) =>
  vi.stubGlobal("fetch", vi.fn(() => (response instanceof Error ? Promise.reject(response) : Promise.resolve(response))));

describe("what the console client says when it cannot get an answer", () => {
  it("answers with a problem, not a rejection, when the console cannot be reached", async () => {
    answering(new TypeError("Failed to fetch"));

    const answer = await api.jobs("plant");

    expect(answer.ok).toBe(false);
    expect(answer).toMatchObject({ ok: false, status: 0 });
    expect("problem" in answer && answer.problem).toMatch(/could not be reached/i);
  });

  it("takes an accepted answer with no body as accepted", async () => {
    answering(new Response("", { status: 202 }));

    expect(await api.cancelJob("plant", "j1")).toEqual({ ok: true, value: undefined });
  });

  it("says so when an answer that was ok cannot be read, instead of throwing", async () => {
    answering(new Response("<html>a proxy page</html>", { status: 200 }));

    const answer = await api.jobs("plant");

    expect(answer).toMatchObject({ ok: false, status: 200 });
    expect("problem" in answer && answer.problem).toMatch(/could not be read/i);
  });

  it("still reads an answer that is there", async () => {
    answering(new Response(JSON.stringify([{ group: "plant" }]), { status: 200 }));

    expect(await api.executors()).toEqual({ ok: true, value: [{ group: "plant" }] });
  });
});
