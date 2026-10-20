"use strict";

const fs = require("node:fs");
const {
  policy,
  modes,
  humanRemovedLabels,
} = require("./policy");
const {
  buildIssueRecord,
  enrichBatchRecords,
  buildDigest,
  renderDigestMarkdown,
  prepareIssueContext,
} = require("./evidence");
const { createGitHubClient } = require("./github");

function admitIssueRun({ mode, aiEnabled, dailyBudget, issue, record, eventName }) {
  const optedIn = (issue.labels || [])
    .map((l) => (typeof l === "string" ? l : l.name))
    .includes(policy.optInLabel);
  const runDeterministic = true;
  const runAi = Boolean(
    aiEnabled &&
    dailyBudget > 0 &&
    !record.analysis.security.sensitive &&
    (mode === "publish-labels" || optedIn || eventName === "workflow_dispatch"),
  );
  const canApplyLabels = Boolean(mode === "publish-labels");
  return {
    runDeterministic,
    runAi,
    canApplyLabels,
    optedIn,
    labelsToAdd: record.analysis.labelsToAdd,
  };
}

async function runSingleIssueAdmission({ client, issueNumber, eventName, automationSha, env = process.env }) {
  const { mode, aiEnabled, dailyBudget } = modes(env);
  const [issue, comments, events, openItems] = await Promise.all([
    client.getIssue(issueNumber),
    client.listIssueComments(issueNumber),
    client.listIssueEvents(issueNumber),
    client.listOpenIssuesAndPrs(6),
  ]);
  if (issue.pull_request) {
    return { skipped: true, reason: "pull_request events are handled by pr-automation" };
  }
  const openPullRequests = openItems.filter((item) => Boolean(item.pull_request));
  const openIssues = openItems.filter((item) => !item.pull_request);

  const allRecords = openIssues.map((candidate) => (
    candidate.number === issue.number
      ? buildIssueRecord(issue, { comments, events, openPrs: openPullRequests })
      : buildIssueRecord(candidate, { openPrs: openPullRequests })
  ));
  enrichBatchRecords(allRecords);
  const record = allRecords.find((r) => r.analysis.number === issue.number) ||
    buildIssueRecord(issue, { comments, events, openPrs: openPullRequests });
  record.comments = comments;

  const decision = admitIssueRun({ mode, aiEnabled, dailyBudget, issue, record, eventName });
  const context = prepareIssueContext(record, automationSha);

  const deterministicReport = {
    version: 1,
    runId: String(env.GITHUB_RUN_ID || "local"),
    number: record.analysis.number,
    fingerprint: record.analysis.fingerprint,
    automationSha,
    mode,
    aiEnabled,
    optedIn: decision.optedIn,
    canApplyLabels: decision.canApplyLabels,
    record,
    context,
    finalLabels: record.analysis.proposedLabels,
    labelsToAdd: record.analysis.labelsToAdd,
  };

  return {
    skipped: false,
    mode,
    aiEnabled,
    decision,
    record,
    context,
    deterministicReport,
  };
}

async function runBacklogDigest({ client, batchLimit = policy.maxBatchIssues, env = process.env }) {
  const { mode, aiEnabled, dailyBudget } = modes(env);
  const openItems = await client.listOpenIssuesAndPrs(6);
  const openPullRequests = openItems.filter((item) => Boolean(item.pull_request));
  const openIssues = openItems.filter((item) => !item.pull_request);

  const detailedSlice = openIssues.slice(0, Math.min(batchLimit, policy.maxBatchIssues));
  const detailedByNumber = new Map();
  for (const item of detailedSlice) {
    const [comments, events] = await Promise.all([
      client.listIssueComments(item.number),
      client.listIssueEvents(item.number),
    ]);
    detailedByNumber.set(
      item.number,
      buildIssueRecord(item, { comments, events, openPrs: openPullRequests }),
    );
  }

  const allRecords = openIssues.map((item) => (
    detailedByNumber.get(item.number) ||
    buildIssueRecord(item, { openPrs: openPullRequests })
  ));

  const digest = buildDigest(allRecords, { mode, aiEnabled });
  const markdown = renderDigestMarkdown(digest);
  return {
    mode,
    aiEnabled,
    dailyBudget,
    digest,
    markdown,
    records: allRecords,
  };
}

async function cli() {
  const [command] = process.argv.slice(2);
  const client = createGitHubClient({
    repo: process.env.GITHUB_REPOSITORY,
    token: process.env.GITHUB_TOKEN,
  });

  if (command === "single") {
    const issueNumber = Number.parseInt(process.env.ISSUE_NUMBER || "", 10);
    if (!Number.isSafeInteger(issueNumber) || issueNumber < 1) {
      throw new Error("Valid ISSUE_NUMBER is required");
    }
    const result = await runSingleIssueAdmission({
      client,
      issueNumber,
      eventName: process.env.GITHUB_EVENT_NAME || "issues",
      automationSha: process.env.GITHUB_SHA || "0".repeat(40),
    });
    fs.mkdirSync("issue-evidence", { recursive: true });
    fs.mkdirSync("issue-automation-results", { recursive: true });
    if (!result.skipped) {
      fs.writeFileSync("issue-evidence/context.json", JSON.stringify(result.context));
      fs.writeFileSync(
        `issue-automation-results/result-${issueNumber}.json`,
        JSON.stringify(result.deterministicReport),
      );
      if (process.env.GITHUB_STEP_SUMMARY) {
        const { renderIssueReport } = require("./validation");
        fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY, `${renderIssueReport(result.deterministicReport)}\n`);
      }
    }
    if (process.env.GITHUB_OUTPUT) {
      fs.appendFileSync(
        process.env.GITHUB_OUTPUT,
        `skipped=${result.skipped}\n` +
        `run-ai=${Boolean(result.decision?.runAi)}\n` +
        `can-apply-labels=${Boolean(result.decision?.canApplyLabels)}\n` +
        `labels-to-add-count=${result.decision?.labelsToAdd?.length || 0}\n`,
      );
    }
    return;
  }

  if (command === "digest") {
    const limit = Number.parseInt(process.env.ISSUE_BATCH_LIMIT || String(policy.maxBatchIssues), 10);
    const { digest, markdown } = await runBacklogDigest({
      client,
      batchLimit: Number.isSafeInteger(limit) && limit > 0 ? limit : policy.maxBatchIssues,
    });
    fs.mkdirSync("issue-automation-results", { recursive: true });
    fs.writeFileSync("issue-automation-results/digest.json", JSON.stringify(digest, null, 2));
    fs.writeFileSync("issue-automation-results/digest.md", markdown);
    if (process.env.GITHUB_STEP_SUMMARY) {
      fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY, `${markdown}\n`);
    }
    return;
  }

  throw new Error("Unknown admission command");
}

if (require.main === module) {
  cli().catch((error) => {
    console.error(error.message || error);
    process.exit(1);
  });
}

module.exports = {
  admitIssueRun,
  runSingleIssueAdmission,
  runBacklogDigest,
  humanRemovedLabels,
};
