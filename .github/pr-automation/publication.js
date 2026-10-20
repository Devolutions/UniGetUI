"use strict";

const fs = require("node:fs");
const path = require("node:path");
const { ROOT, modes, route, workflows, SHA } = require("./policy");
const { sameIdentity, currentPr, changedFiles, optIn, checkCi } = require("./github");
const { readJson, validate, render } = require("./validation");
const { policy } = require("./policy");
const { reviewGate, reviewMarker, desiredLabels, stateComment, readLease, stateBody } = require("./lifecycle");
const manifest = require("./labels.json");
const owned = new Set(manifest.map((entry) => entry.name).filter((name) => name !== policy.optInLabel));

function validReport(report, snapshot) {
  if (report?.version !== 1 || report.runId !== snapshot.runId || report.number !== snapshot.number ||
      report.head !== snapshot.head || report.base !== snapshot.base || report.automationSha !== snapshot.automationSha ||
      report.context?.head !== snapshot.head || report.context.base !== snapshot.base ||
      report.context.automationSha !== snapshot.automationSha ||
      !Array.isArray(report.specialists) || !validate(report.classifier, "classifier", report.context).ok) {
    throw new Error("Review report provenance is invalid");
  }
  const selected = route(snapshot.analysis, report.classifier);
  if (selected.length !== report.specialists.length ||
      selected.some((stage, index) => report.specialists[index].reviewer !== stage)) {
    throw new Error("Required specialist output missing");
  }
  for (const output of report.specialists) {
    if (!validate(output, output.reviewer, report.context).ok) throw new Error("Specialist report is invalid");
  }
  if (!validate(report.review, "general", report.context, report.specialists).ok) throw new Error("Final review is invalid");
  return report;
}

async function reconcileLabels(github, repo, pr, labels, beforeWrite) {
  if (labels.some((label) => !owned.has(label))) throw new Error("Attempted mutation of an unowned label");
  const available = await github.paginate(github.rest.issues.listLabelsForRepo, { ...repo, per_page: 100 });
  // Validate the entire manifest before starting any mutation; setup is a separate human operation.
  if (manifest.some((entry) => !available.some((label) => label.name === entry.name))) {
    throw new Error("Approved automation labels have not been provisioned");
  }
  const current = new Set(pr.labels.map((label) => label.name));
  const desired = new Set(labels);
  const add = [...desired].filter((label) => !current.has(label));
  if (add.length) {
    await beforeWrite();
    await github.rest.issues.addLabels({ ...repo, issue_number: pr.number, labels: add });
  }
  for (const name of current) if (owned.has(name) && !desired.has(name)) {
    await beforeWrite();
    await github.rest.issues.removeLabel({ ...repo, issue_number: pr.number, name });
  }
}

async function publishOne(github, repo, snapshot, report, env, now = () => Date.now()) {
  if (modes(env).mode !== "publish") return { status: "report-only" };
  if (!snapshot.analysis || !SHA.test(snapshot.automationSha) || snapshot.runId !== env.GITHUB_RUN_ID ||
      snapshot.automationSha !== env.AUTOMATION_SHA) throw new Error("Snapshot provenance is invalid");
  if (report) validReport(report, snapshot);
  const pr = await currentPr(github, repo, snapshot.number);
  if (pr.head.sha !== snapshot.head || pr.base.sha !== snapshot.base) return { status: "superseded" };
  const beforeWrite = async () => {
    const current = await currentPr(github, repo, snapshot.number);
    if (current.head.sha !== snapshot.head || current.base.sha !== snapshot.base ||
        current.state !== pr.state || current.draft !== pr.draft) throw new Error("PR changed before mutation");
    if (modes(env).mode !== "publish") throw new Error("Publication disabled");
  };
  let reviews = await github.paginate(github.rest.pulls.listReviews, { ...repo, pull_number: pr.number, per_page: 100 });
  let failed = false;
  let status = snapshot.status;
  const gate = reviewGate(reviews, snapshot.head);
  if (sameIdentity(pr, snapshot) && snapshot.status === "ready" && !gate.duplicate && gate.count < policy.maxReviews) {
    if (!modes(env).aiEnabled || !await optIn(github, repo, pr)) return { status: "disabled" };
    const files = await changedFiles(github, repo, pr);
    const ci = await checkCi(github, repo, pr, workflows(ROOT, pr.base.ref, files), snapshot.observedRun);
    if (!ci.ok) return { status: "waiting" };
    if (!report) { failed = true; status = "failed"; }
    else {
      const comments = await github.paginate(github.rest.issues.listComments, { ...repo, issue_number: pr.number, per_page: 100 });
      const comment = stateComment(comments);
      const lease = readLease(comment, now());
      if (lease && lease.expires > now() && lease.run !== snapshot.runId) return { status: "leased" };
      // Label provisioning is checked before a lease/comment/review can be written.
      const labels = await github.paginate(github.rest.issues.listLabelsForRepo, { ...repo, per_page: 100 });
      if (manifest.some((entry) => !labels.some((label) => label.name === entry.name))) {
        throw new Error("Approved automation labels have not been provisioned");
      }
      await beforeWrite();
      const body = stateBody(snapshot, now() + 10 * 60 * 1000, "Publication lease held by the current workflow.");
      const state = comment
        ? (await github.rest.issues.updateComment({ ...repo, comment_id: comment.id, body })).data
        : (await github.rest.issues.createComment({ ...repo, issue_number: pr.number, body })).data;
      await beforeWrite();
      // Re-list after acquiring ownership to recover safely after a previous API success with a lost response.
      reviews = await github.paginate(github.rest.pulls.listReviews, { ...repo, pull_number: pr.number, per_page: 100 });
      if (!reviewGate(reviews, snapshot.head).duplicate) {
        const finalCi = await checkCi(github, repo, pr, workflows(ROOT, pr.base.ref, files), snapshot.observedRun);
        if (!finalCi.ok) throw new Error("CI generation changed before review publication");
        await beforeWrite();
        await github.rest.pulls.createReview({
          ...repo, pull_number: pr.number, commit_id: snapshot.head, event: "COMMENT",
          body: `${reviewMarker(snapshot, report)}\n${render(report.review)}`,
        });
      }
      reviews = await github.paginate(github.rest.pulls.listReviews, { ...repo, pull_number: pr.number, per_page: 100 });
      if (!reviewGate(reviews, snapshot.head).duplicate) throw new Error("Published review could not be confirmed");
      await beforeWrite();
      await github.rest.issues.updateComment({
        ...repo, comment_id: state.id, body: stateBody(snapshot, 0, "Validated advisory review published."),
      });
      status = "completed";
    }
  }
  // A failure is orthogonal to which human acts next. Never label a disabled model as failed.
  await reconcileLabels(github, repo, pr, desiredLabels(snapshot, {
    report, failed, reviews, closed: pr.state !== "open" || pr.draft,
  }), beforeWrite);
  return { status };
}

async function publication({ github, context, core }) {
  if (modes(process.env).mode !== "publish") return;
  const directory = "automation-snapshots";
  for (const file of fs.readdirSync(directory)) {
    if (!/^snapshot-\d+\.json$/.test(file)) throw new Error("Unexpected snapshot artifact");
    const snapshot = readJson(path.join(directory, file));
    if (!snapshot.analysis) {
      core.warning(`PR ${snapshot.number}: admission failed or was skipped; no mutation`);
      continue;
    }
    const resultPath = `automation-results/result-${snapshot.number}.json`;
    const report = fs.existsSync(resultPath) ? readJson(resultPath) : null;
    const result = await publishOne(github, context.repo, snapshot, report, process.env);
    await core.summary.addRaw(`PR #${snapshot.number}: publication **${result.status}**.\n\n`).write();
    if (result.status === "failed") core.setFailed("Enabled review pipeline did not produce validated output");
  }
}

module.exports = { validReport, reconcileLabels, publishOne, publication };
