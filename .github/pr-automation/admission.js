"use strict";

const fs = require("node:fs");
const { ROOT, modes, analyze, workflows, policy } = require("./policy");
const { identity, sameIdentity, resolve, currentPr, optIn, changedFiles, checkCi } = require("./github");
const { reviewGate } = require("./lifecycle");
const { escape } = require("./validation");

async function admitOne(github, context, number, env) {
  const settings = modes(env);
  const repo = context.repo;
  const pr = await currentPr(github, repo, number);
  const expected = identity(pr);
  const metadata = (await github.rest.repos.get(repo)).data;
  if (pr.base.repo.full_name !== `${repo.owner}/${repo.repo}` || pr.base.ref !== metadata.default_branch) {
    return { number, status: "skipped", reason: "PR does not target the default branch" };
  }
  const files = await changedFiles(github, repo, pr);
  const analysis = analyze(pr, files);
  const snapshot = {
    version: 1, ...expected, runId: String(context.runId), automationSha: context.sha,
    baseRef: pr.base.ref, files, analysis, settings, status: "disabled", reason: "AI disabled",
  };
  if (context.eventName === "workflow_run") {
    const run = context.payload.workflow_run;
    snapshot.observedRun = {
      id: run.id, run_attempt: run.run_attempt, path: run.path, event: run.event,
      head_sha: run.head_sha, status: run.status, conclusion: run.conclusion,
      repository: { full_name: run.repository.full_name }, pull_requests: run.pull_requests,
    };
  }
  const required = workflows(ROOT, pr.base.ref, files);
  snapshot.required = required;
  if (pr.state !== "open" || pr.draft) {
    snapshot.status = "skipped"; snapshot.reason = "PR is closed or draft";
    return snapshot;
  }
  const reviews = await github.paginate(github.rest.pulls.listReviews, { ...repo, pull_number: number, per_page: 100 });
  const gate = reviewGate(reviews, expected.head);
  if (gate.duplicate || gate.count >= policy.maxReviews) {
    snapshot.status = "skipped"; snapshot.reason = gate.duplicate ? "head already reviewed" : "distinct-head review cap reached";
  } else if (settings.aiEnabled) {
    if (!await optIn(github, repo, pr)) {
      snapshot.status = "skipped"; snapshot.reason = "AI requires a maintainer-authorized opt-in label";
    } else {
      snapshot.ci = await checkCi(github, repo, pr, required, snapshot.observedRun);
      snapshot.status = snapshot.ci.ok ? "ready" : "waiting";
      snapshot.reason = snapshot.ci.ok ? "current applicable CI is green" : snapshot.ci.reason;
    }
  }
  if (!sameIdentity(await currentPr(github, repo, number), expected)) {
    snapshot.status = "superseded"; snapshot.reason = "PR changed during admission";
  }
  return snapshot;
}

async function admission({ github, context, core }) {
  // github.workflow_sha is the trusted revision even when github.sha is a PR merge/head SHA.
  const execution = { ...context, sha: process.env.AUTOMATION_SHA };
  modes(process.env);
  const numbers = await resolve(github, execution);
  if (numbers.length > 20) throw new Error("Too many PRs associated with one CI event");
  fs.mkdirSync("automation-snapshots");
  const ready = [];
  for (const number of numbers) {
    let snapshot;
    try { snapshot = await admitOne(github, execution, number, process.env); }
    catch (error) {
      core.error(`PR ${number} admission failed: ${error.message}`);
      snapshot = { number, status: "failed", reason: "admission failed; inspect the workflow failure" };
      core.setFailed("PR admission failed");
    }
    fs.writeFileSync(`automation-snapshots/snapshot-${number}.json`, JSON.stringify(snapshot));
    await core.summary.addRaw(`PR #${number}: **${snapshot.status}** - ${escape(snapshot.reason)}.\n\n`)
      .addCodeBlock(JSON.stringify(snapshot.analysis || {}, null, 2), "json").write();
    if (snapshot.status === "ready") ready.push({ number, head: snapshot.head, base: snapshot.base });
  }
  core.setOutput("matrix", JSON.stringify({ include: ready }));
  core.setOutput("has-reviews", ready.length > 0);
  core.setOutput("has-prs", numbers.length > 0);
}

module.exports = { admitOne, admission };
