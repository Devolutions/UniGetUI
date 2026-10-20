"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { publishOne, validReport } = require("../publication");
const { reviewMarker, reviewGate, desiredLabels, authorAction, readLease, stateBody } = require("../lifecycle");
const { snapshot, report, repo, env, fake, HEAD, BASE, AUTOMATION } = require("./fixtures");

function prior(head, base = BASE) {
  const input = { ...snapshot(), head, base };
  const output = report(input);
  return { id: 1, state: "COMMENTED", body: reviewMarker(input, output), commit_id: head,
    user: { login: "github-actions[bot]", type: "Bot" } };
}
test("default publication mode returns without even reading GitHub", async () => {
  const result = await publishOne({}, repo, {}, null, {});
  assert.deepEqual(result, { status: "report-only" });
});
test("preview and missing/incorrect provenance cannot mutate", async () => {
  const { github, state } = fake();
  const input = snapshot();
  await publishOne(github, repo, input, report(input), { ...env, AUTOMATION_MODE: "report-only" });
  assert.deepEqual(state.writes, []);
  await assert.rejects(publishOne(github, repo, { ...input, runId: "different" }, report(input), env), /provenance/);
  const invalid = { ...report(input), head: AUTOMATION };
  await assert.rejects(publishOne(github, repo, input, invalid, env), /provenance/);
  assert.deepEqual(state.writes, []);
});
test("valid advisory publication is comment-only, counted once and preserves human labels", async () => {
  const { github, state } = fake();
  state.pr.labels.push({ name: "ready-for-release" }, { name: "needs-author-answer" });
  const input = snapshot(), output = report(input);
  assert.deepEqual(await publishOne(github, repo, input, output, env), { status: "completed" });
  const writes = state.writes.filter(([kind]) => kind === "review");
  assert.equal(writes.length, 1);
  assert.equal(writes[0][1].event, "COMMENT");
  assert.equal(writes[0][1].commit_id, HEAD);
  assert.equal(state.pr.labels.some((label) => label.name === "ready-for-release"), true);
  assert.equal(state.pr.labels.some((label) => label.name === "needs-author-answer"), true);
  assert.equal(state.pr.labels.some((label) => label.name === "ai-reviewed/1"), true);
  await publishOne(github, repo, input, null, env);
  assert.equal(state.writes.filter(([kind]) => kind === "review").length, 1);
  assert.equal(state.pr.labels.some((label) => label.name === "risk/low"), true);
});
test("changed source or base suppresses every write", async () => {
  for (const field of ["head", "base"]) {
    const { github, state } = fake();
    state.pr[field].sha = AUTOMATION;
    assert.equal((await publishOne(github, repo, snapshot(), report(), env)).status, "superseded");
    assert.deepEqual(state.writes, []);
  }
});
test("a push between lease and review is detected immediately", async () => {
  const { github, state } = fake();
  const create = github.rest.issues.createComment;
  github.rest.issues.createComment = async (args) => {
    const response = await create(args);
    state.pr.head.sha = AUTOMATION;
    return response;
  };
  await assert.rejects(publishOne(github, repo, snapshot(), report(), env), /changed before mutation/);
  assert.equal(state.writes.some(([kind]) => kind === "review"), false);
  assert.equal(state.writes.some(([kind]) => kind === "labels"), false);
});
test("unprovisioned labels fail before comments, leases or reviews", async () => {
  const { github, state } = fake({ labels: [] });
  await assert.rejects(publishOne(github, repo, snapshot(), report(), env), /provisioned/);
  assert.deepEqual(state.writes, []);
});
test("failed required stages produce explicit failure independently of next actor", async () => {
  const { github, state } = fake();
  const result = await publishOne(github, repo, snapshot(), null, env);
  assert.equal(result.status, "failed");
  const names = state.pr.labels.map((entry) => entry.name);
  assert.equal(names.includes("automation-failed"), true);
  assert.equal(names.includes("needs-review"), true);
  assert.equal(state.reviews.length, 0);
});
test("disabled AI does not publish a report even in publication mode", async () => {
  const { github, state } = fake();
  assert.equal((await publishOne(github, repo, snapshot(), report(), { ...env, AUTOMATION_AI_ENABLED: "false" })).status, "disabled");
  assert.deepEqual(state.writes, []);
});
test("cap counts published distinct heads, not duplicates, human spoofs or preview output", () => {
  const reviews = [prior(HEAD), prior(HEAD), prior(BASE), prior(AUTOMATION)];
  assert.equal(reviewGate(reviews, HEAD).count, 3);
  assert.equal(reviewGate(reviews, HEAD).duplicate, true);
  assert.equal(reviewGate([{ ...prior(HEAD), user: { login: "contributor", type: "User" } }], HEAD).count, 0);
});
test("publication never exceeds the three-distinct-head cap", async () => {
  const { github, state } = fake({ reviews: [prior(BASE), prior(AUTOMATION), prior("e".repeat(40))] });
  const result = await publishOne(github, repo, snapshot(), report(), env);
  assert.equal(result.status, "ready");
  assert.equal(state.writes.filter(([kind]) => kind === "review").length, 0);
  assert.equal(state.pr.labels.some((label) => label.name === "ai-reviewed/3"), true);
});
test("a successful review with a lost API response is recovered without duplicating it", async () => {
  const { github, state } = fake();
  const create = github.rest.pulls.createReview;
  github.rest.pulls.createReview = async (args) => {
    await create(args);
    throw new Error("Response interrupted");
  };
  await assert.rejects(publishOne(github, repo, snapshot(), report(), env), /interrupted/);
  assert.equal(state.reviews.length, 1);
  github.rest.pulls.createReview = () => { throw new Error("Must not repeat successful API mutation"); };
  await publishOne(github, repo, snapshot(), null, env);
  assert.equal(state.reviews.length, 1);
  assert.equal(state.pr.labels.some((label) => label.name === "ai-reviewed/1"), true);
});
test("revoked opt-in and stale CI cannot produce a review or failure label", async () => {
  const { github, state } = fake({ optInEvents: [] });
  assert.equal((await publishOne(github, repo, snapshot(), report(), env)).status, "disabled");
  assert.deepEqual(state.writes, []);
});
test("lease expiry and owner prevent a second writer while allowing bounded recovery", async () => {
  const input = snapshot(), now = 1000000;
  const other = { ...input, runId: "999" };
  const { github, state } = fake({ comments: [{ id: 1, user: { login: "github-actions[bot]", type: "Bot" },
    body: stateBody(other, now + 1000, "Publication in progress.") }] });
  assert.equal((await publishOne(github, repo, input, report(), env, () => now)).status, "leased");
  assert.deepEqual(state.writes, []);
  assert.equal((await publishOne(github, repo, input, report(), env, () => now + 1001)).status, "completed");
  assert.throws(() => readLease({ body: "invalid" }, now), /malformed/);
});
test("human review and advisory findings inform next actor without consuming human labels", () => {
  const changes = [{ id: 1, state: "CHANGES_REQUESTED", user: { login: "reviewer", type: "User" } }];
  assert.equal(authorAction(changes), true);
  assert.equal(desiredLabels(snapshot(), { reviews: changes, failed: true }).includes("needs-author-action"), true);
  assert.equal(authorAction([...changes, { ...changes[0], id: 2, state: "APPROVED" }]), false);
  assert.equal(desiredLabels(snapshot(), { reviews: changes, closed: true }).includes("needs-author-action"), false);
});
test("missing mandatory specialist or missing candidate dispositions cannot pass final writer", () => {
  const input = snapshot();
  input.analysis.reviewers = ["dotnet-aot"];
  assert.throws(() => validReport(report(input), input), /specialist/);
});
