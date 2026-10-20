"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const YAML = require("yaml");

const { runSingleIssueAdmission, runBacklogDigest } = require("../admission");
const { publishIssueLabels } = require("../publication");
const { configuration } = require("../stages");
const { agentValidator } = require("../validation");

const root = path.resolve(__dirname, "..", "..", "..");

test("issue-automation workflow enforces least-privilege permissions, loop guards, and serialized label writer", () => {
  const wf = YAML.parse(
    fs.readFileSync(path.join(root, ".github/workflows/issue-automation.yml"), "utf8"),
  );

  assert.equal(wf.permissions.contents, "read");
  assert.equal(wf.jobs["analyze-issue"].permissions.issues, "read");
  assert.equal(wf.jobs["ai-triage"].permissions.issues, undefined);
  assert.equal(wf.jobs["backlog-digest"].permissions.issues, "read");

  const publishJob = wf.jobs["publish-labels"];
  assert.equal(publishJob.permissions.issues, "write");
  assert.equal(publishJob.concurrency["cancel-in-progress"], false);
  assert.equal(publishJob.concurrency.queue, "max");

  assert.ok(wf.jobs["analyze-issue"].if.includes("github.actor != 'github-actions[bot]'"));
});

test("issue templates support cross-platform reporting and avoid destructive troubleshooting checkboxes", () => {
  const bug = YAML.parse(
    fs.readFileSync(path.join(root, ".github/ISSUE_TEMPLATE/bug-issue.yml"), "utf8"),
  );
  const crash = YAML.parse(
    fs.readFileSync(path.join(root, ".github/ISSUE_TEMPLATE/hard-crash.yml"), "utf8"),
  );
  const config = YAML.parse(
    fs.readFileSync(path.join(root, ".github/ISSUE_TEMPLATE/config.yml"), "utf8"),
  );

  const crashRaw = JSON.stringify(crash);
  assert.ok(!crashRaw.includes("I have tried deleting a folder named"));
  assert.ok(crash.body.some((item) => item.attributes?.label === "UniGetUI Version"));
  assert.ok(crash.body.some((item) => item.attributes?.label?.includes("Operating system")));

  const pmLogsField = bug.body.find((item) => item.attributes?.label === "Package Managers Logs");
  assert.equal(pmLogsField.validations.required, false);

  assert.ok(
    config.contact_links[0].url.endsWith("/docs/ISSUE_DIAGNOSTICS.md"),
  );
});

test("admission, digest, stages configuration, and publication work end-to-end with mocked GitHub API", async () => {
  const sampleIssue = {
    number: 5541,
    state: "open",
    title: "[BUG] Headless daemon fails to initialize UniGetUI Elevator",
    user: { login: "headlessuser" },
    labels: [{ name: "bug" }, { name: "automation/ai-triage" }],
    body: "### UniGetUI Version\n2026.3.2\n### Operating system, version, and architecture\nWindows 11 24H2 x64\n### Describe your issue\nRunning `UniGetUI --headless` fails in LoadElevatorAsync.",
  };
  const addedCalls = [];
  const mockClient = {
    getIssue: async (num) => (num === 9999 ? { number: 9999, pull_request: {} } : sampleIssue),
    listIssueComments: async () => [],
    listIssueEvents: async () => [],
    listOpenIssuesAndPrs: async () => [
      sampleIssue,
      {
        number: 5545,
        pull_request: {},
        title: "fix(headless): initialize UniGetUI elevator in headless daemon mode",
        body: "Fixes #5541",
      },
    ],
    addLabels: async (num, labels) => {
      addedCalls.push({ num, labels });
      return labels;
    },
  };

  const skippedPr = await runSingleIssueAdmission({
    client: mockClient,
    issueNumber: 9999,
    eventName: "issues",
    automationSha: "a".repeat(40),
    env: {},
  });
  assert.equal(skippedPr.skipped, true);

  const admitted = await runSingleIssueAdmission({
    client: mockClient,
    issueNumber: 5541,
    eventName: "issues",
    automationSha: "a".repeat(40),
    env: {
      ISSUE_AUTOMATION_MODE: "report-only",
      ISSUE_AUTOMATION_AI_ENABLED: "true",
      ISSUE_AUTOMATION_DAILY_BUDGET: "10",
      ISSUE_AUTOMATION_API_BASE_URL: "https://api.example.com/v1",
      ISSUE_AUTOMATION_MODEL: "gpt-test",
    },
  });
  assert.equal(admitted.skipped, false);
  assert.equal(admitted.decision.runAi, true);
  assert.equal(admitted.decision.canApplyLabels, false);
  assert.ok(admitted.deterministicReport.labelsToAdd.includes("area/elevation-agent"));

  const digestRes = await runBacklogDigest({
    client: mockClient,
    batchLimit: 5,
    env: {},
  });
  assert.equal(digestRes.digest.totalAnalyzed, 1);
  assert.ok(digestRes.markdown.includes("#5541"));

  const stageCfg = configuration("triage", {
    ISSUE_AUTOMATION_MODE: "report-only",
    ISSUE_AUTOMATION_AI_ENABLED: "true",
    ISSUE_AUTOMATION_DAILY_BUDGET: "5",
    ISSUE_AUTOMATION_API_BASE_URL: "https://api.example.com/v1",
    ISSUE_AUTOMATION_MODEL: "gpt-test",
  });
  assert.equal(stageCfg.id, "triage");
  assert.ok(stageCfg.allowed_files.includes("issue-evidence/context.json"));

  const tmpDir = fs.mkdtempSync(path.join(root, ".github/issue-automation/test-tmp-"));
  try {
    const contextFile = path.join(tmpDir, "context.json");
    const reportFile = path.join(tmpDir, "report.json");
    fs.writeFileSync(contextFile, JSON.stringify(admitted.context));
    fs.writeFileSync(reportFile, JSON.stringify(admitted.deterministicReport));

    const validRes = agentValidator(
      {
        issueNumber: 5541,
        fingerprint: admitted.deterministicReport.fingerprint,
        abstain: true,
        issueKind: "bug",
        proposedLabels: admitted.deterministicReport.finalLabels,
        ownershipHypothesis: {
          category: "unigetui-integration",
          confidence: "high",
          basis: "LoadElevatorAsync missing in HeadlessDaemonHost",
        },
        evidenceCitations: [{ source: "title", commentId: null, quote: "Headless daemon fails" }],
        relatedCandidates: [],
        capabilityMatches: [],
        missingDiagnostics: [],
        recommendedQueue: "current-regressions",
        summary: "Headless elevator initialization defect.",
      },
      { metadata: { stage: "triage", context_file: contextFile } },
    );
    assert.deepEqual(validRes, { ok: true });

    const pubReportOnly = await publishIssueLabels({
      client: mockClient,
      reportFile,
      env: { ISSUE_AUTOMATION_MODE: "report-only" },
    });
    assert.equal(pubReportOnly.apply, false);
    assert.equal(addedCalls.length, 0);

    const pubActive = await publishIssueLabels({
      client: mockClient,
      reportFile,
      env: { ISSUE_AUTOMATION_MODE: "publish-labels" },
    });
    assert.equal(pubActive.apply, true);
    assert.equal(addedCalls.length, 1);
    assert.ok(addedCalls[0].labels.includes("area/elevation-agent"));
  } finally {
    fs.rmSync(tmpDir, { recursive: true, force: true });
    fs.rmSync(path.join(root, "issue-evidence"), { recursive: true, force: true });
  }
});
