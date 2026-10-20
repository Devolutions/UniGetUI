"use strict";

const fs = require("node:fs");
const {
  FINGERPRINT,
  OWNED_LABELS,
  PROTECTED_LABELS,
  modes,
  contentFingerprint,
  humanRemovedLabels,
} = require("./policy");
const { readJson } = require("./validation");
const { createGitHubClient } = require("./github");

function verifyPublicationPlan({ report, liveIssue, liveComments, liveEvents, env = process.env }) {
  const { mode } = modes(env);
  if (!report || !Number.isSafeInteger(report.number) || !FINGERPRINT.test(report.fingerprint)) {
    throw new Error("Invalid issue automation report artifact");
  }
  if (!liveIssue || liveIssue.number !== report.number || liveIssue.pull_request) {
    throw new Error("Live issue does not match report target");
  }
  if (liveIssue.state !== "open") {
    return { apply: false, reason: "issue is no longer open", labelsToAdd: [] };
  }

  const liveFingerprint = contentFingerprint(liveIssue, liveComments);
  if (liveFingerprint !== report.fingerprint) {
    return { apply: false, reason: "issue content changed since analysis fingerprint", labelsToAdd: [] };
  }

  const existingLabels = new Set(
    (liveIssue.labels || []).map((l) => (typeof l === "string" ? l : l.name)),
  );
  if (mode !== "publish-labels") {
    return { apply: false, reason: `mode '${mode}' is advisory for this issue`, labelsToAdd: [] };
  }

  const removedByHuman = humanRemovedLabels(liveEvents);
  const candidateLabels = Array.isArray(report.finalLabels) ? report.finalLabels : [];
  const labelsToAdd = [];
  for (const label of candidateLabels) {
    if (!OWNED_LABELS.has(label) || PROTECTED_LABELS.has(label)) {
      throw new Error(`Refusing to apply unowned or protected label '${label}'`);
    }
    if (!existingLabels.has(label) && !removedByHuman.has(label)) {
      labelsToAdd.push(label);
    }
  }

  return {
    apply: labelsToAdd.length > 0,
    reason: labelsToAdd.length > 0 ? "verified additive labels ready" : "no new additive labels needed",
    labelsToAdd,
  };
}

async function publishIssueLabels({ client, reportFile, env = process.env }) {
  const report = readJson(reportFile);
  const [liveIssue, liveComments, liveEvents] = await Promise.all([
    client.getIssue(report.number),
    client.listIssueComments(report.number),
    client.listIssueEvents(report.number),
  ]);
  const plan = verifyPublicationPlan({ report, liveIssue, liveComments, liveEvents, env });
  if (plan.apply) {
    await client.addLabels(report.number, plan.labelsToAdd);
  }
  return plan;
}

async function cli() {
  const [reportFile] = process.argv.slice(2);
  if (!reportFile) throw new Error("Report file path is required");
  const client = createGitHubClient({
    repo: process.env.GITHUB_REPOSITORY,
    token: process.env.GITHUB_TOKEN,
  });
  const plan = await publishIssueLabels({ client, reportFile });
  if (process.env.GITHUB_STEP_SUMMARY) {
    fs.appendFileSync(
      process.env.GITHUB_STEP_SUMMARY,
      `\n- **Label publication:** ${plan.apply ? `applied ${plan.labelsToAdd.map((l) => `\`${l}\``).join(", ")}` : `skipped (${plan.reason})`}\n`,
    );
  }
}

if (require.main === module) {
  cli().catch((error) => {
    console.error(error.message || error);
    process.exit(1);
  });
}

module.exports = {
  verifyPublicationPlan,
  publishIssueLabels,
};
