"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { checkCi, optIn, resolve } = require("../github");
const { admitOne } = require("../admission");
const { HEAD, BASE, AUTOMATION, MERGE, repo, file, run, env, fake } = require("./fixtures");
const required = [{ file: "dotnet-test.yml", name: ".NET Tests" }];

test("CI missing, pending, failed, neutral and cancelled are not green", async () => {
  for (const runs of [[], [run({ status: "in_progress", conclusion: null })],
    ...["failure", "neutral", "cancelled", "skipped"].map((conclusion) => [run({ conclusion })])]) {
    const { github, state } = fake({ runs });
    assert.equal((await checkCi(github, repo, state.pr, required)).ok, false);
  }
});
test("latest run and latest attempt win over old success", async () => {
  for (const newer of [run({ id: 2, conclusion: "failure" }), run({ run_attempt: 2, conclusion: "failure" })]) {
    const { github, state } = fake({ runs: [run(), newer] });
    assert.equal((await checkCi(github, repo, state.pr, required)).ok, false);
  }
});
test("an authoritative newer CI event cannot be hidden by run-list lag", async () => {
  const { github, state } = fake({ runs: [run()] });
  const observed = run({ id: 2, conclusion: "failure" });
  assert.equal((await checkCi(github, repo, state.pr, required, observed)).ok, false);
  state.runs = [];
  assert.equal((await checkCi(github, repo, state.pr, required, run())).ok, true);
  state.runs = [run({ id: 3, conclusion: "failure" })];
  assert.equal((await checkCi(github, repo, state.pr, required, run())).ok, false);
});
test("foreign workflow, PR, source-head and base associations never satisfy CI", async () => {
  for (const changed of [
    { event: "push" }, { repository: { full_name: "someone/UniGetUI" } },
    { pull_requests: [{ number: 43, head: { sha: HEAD }, base: { sha: BASE } }] },
    { pull_requests: [{ number: 42, head: { sha: AUTOMATION }, base: { sha: BASE } }] },
    { pull_requests: [{ number: 42, head: { sha: HEAD }, base: { sha: AUTOMATION } }] },
  ]) {
    const { github, state } = fake({ runs: [run(changed)] });
    assert.equal((await checkCi(github, repo, state.pr, required)).ok, false);
  }
});
test("green aggregate CI handles verified synthetic merges as well as source SHA runs", async () => {
  const { github, state } = fake({ runs: [run({ head_sha: MERGE })] });
  state.pr.merge_commit_sha = MERGE;
  const result = await checkCi(github, repo, state.pr, required);
  assert.equal(result.ok, true);
  assert.deepEqual(result.generations, [{ workflow: "dotnet-test.yml", run: 1, attempt: 1, sha: MERGE }]);
  github.rest.repos.getCommit = async () => ({ data: { parents: [{ sha: HEAD }, { sha: AUTOMATION }] } });
  assert.equal((await checkCi(github, repo, state.pr, required)).ok, false);
});
test("fork CI without PR payloads can bind through the current verified merge SHA", async () => {
  const { github, state } = fake({ runs: [run({ head_sha: MERGE, pull_requests: [] })] });
  state.pr.merge_commit_sha = MERGE;
  assert.equal((await checkCi(github, repo, state.pr, required)).ok, true);
});
test("full-run metadata can recover a source-SHA fork association; ambiguous newer runs block", async () => {
  const { github, state } = fake({ runs: [run({ pull_requests: [] })] });
  github.rest.actions.getWorkflowRun = async () => ({ data: run() });
  assert.equal((await checkCi(github, repo, state.pr, required)).ok, true);
  state.runs = [run(), run({ id: 2, pull_requests: [] })];
  github.rest.actions.getWorkflowRun = async () => ({ data: state.runs[1] });
  assert.equal((await checkCi(github, repo, state.pr, required)).ok, false);
});
test("no applicable CI is different from missing required CI", async () => {
  const { github, state } = fake();
  assert.deepEqual(await checkCi(github, repo, state.pr, []), { ok: true, generations: [] });
});
test("a label alone is insufficient; its latest grant must be by a current maintainer", async () => {
  const { github, state } = fake({ permission: "read" });
  assert.equal(await optIn(github, repo, state.pr), false);
  state.permission = "maintain";
  assert.equal(await optIn(github, repo, state.pr), true);
  state.optInEvents = [];
  assert.equal(await optIn(github, repo, state.pr), false);
});
test("default admission only reports deterministic data, never invokes opt-in or provider", async () => {
  const { github, state } = fake();
  github.rest.issues.listEvents = () => { throw new Error("Unexpected opt-in lookup"); };
  const result = await admitOne(github, { repo, runId: 123, sha: AUTOMATION }, 42, {});
  assert.equal(result.status, "disabled");
  assert.equal(result.reason, "AI disabled");
  assert.equal(result.analysis.labels.includes("size/XS"), true);
  assert.deepEqual(state.writes, []);
});
test("enabled review waits for applicable CI, then becomes ready", async () => {
  const { github, state } = fake({ files: [file("src/A.cs")] });
  let result = await admitOne(github, { repo, runId: 123, sha: AUTOMATION }, 42, env);
  assert.equal(result.status, "waiting");
  state.runs = [run(), run({ path: ".github/workflows/cli-headless-e2e.yml" })];
  result = await admitOne(github, { repo, runId: 123, sha: AUTOMATION }, 42, env);
  assert.equal(result.status, "ready");
  assert.deepEqual(state.writes, []);
});
test("draft/closed PRs do not request model work", async () => {
  for (const change of [{ draft: true }, { state: "closed" }]) {
    const { github, state } = fake();
    Object.assign(state.pr, change);
    assert.equal((await admitOne(github, { repo, runId: 123, sha: AUTOMATION }, 42, env)).status, "skipped");
  }
});
test("manual dispatch is authorized and CI events use PR identities, not branch names", async () => {
  const { github, state } = fake({ permission: "read" });
  const context = { repo, actor: "someone", eventName: "workflow_dispatch", payload: { inputs: { "pr-number": "42" } } };
  await assert.rejects(resolve(github, context), /permission/);
  state.permission = "write";
  assert.deepEqual(await resolve(github, context), [42]);
  assert.deepEqual(await resolve(github, { repo, eventName: "workflow_run",
    payload: { workflow_run: run({ pull_requests: [] }) } }), [42]);
  assert.deepEqual(await resolve(github, { repo, eventName: "workflow_run",
    payload: { workflow_run: run({ event: "push" }) } }), []);
});
