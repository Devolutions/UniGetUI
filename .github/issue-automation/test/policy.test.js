"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const {
  modes,
  redactText,
  curateBody,
  extractVersionInfo,
  detectManagers,
  analyzeIssue,
  contentFingerprint,
  humanRemovedLabels,
  PROTECTED_LABELS,
} = require("../policy");
const { validate } = require("../validation");
const { verifyPublicationPlan } = require("../publication");

test("modes enforces conservative defaults and AI budget/endpoint constraints", () => {
  const def = modes({});
  assert.equal(def.mode, "report-only");
  assert.equal(def.aiEnabled, false);
  assert.equal(def.dailyBudget, 0);

  assert.throws(() => modes({ ISSUE_AUTOMATION_MODE: "invalid" }), /Invalid issue automation mode/);
  assert.throws(
    () => modes({ ISSUE_AUTOMATION_AI_ENABLED: "true", ISSUE_AUTOMATION_DAILY_BUDGET: "0" }),
    /positive finite ISSUE_AUTOMATION_DAILY_BUDGET/,
  );
  assert.throws(
    () => modes({
      ISSUE_AUTOMATION_AI_ENABLED: "true",
      ISSUE_AUTOMATION_DAILY_BUDGET: "10",
      ISSUE_AUTOMATION_API_BASE_URL: "http://insecure.example.com/v1",
      ISSUE_AUTOMATION_MODEL: "gpt-test",
    }),
    /approved HTTPS endpoint/,
  );
});

test("redactText and curateBody sanitize local user paths, tokens, and bodies over 64 KiB", () => {
  const raw = "Path C:\\Users\\Alice\\AppData\\Local\\UniGetUI and Bearer ghp_1234567890abcdef and /Users/bob/.config";
  const redacted = redactText(raw);
  assert.ok(!redacted.includes("Alice"));
  assert.ok(!redacted.includes("bob"));
  assert.ok(!redacted.includes("ghp_1234567890abcdef"));
  assert.ok(redacted.includes("C:\\Users\\<redacted-user>\\"));

  const huge = "A".repeat(40000) + "\nTAIL_MARKER_" + "B".repeat(30000);
  const curated = curateBody(huge, 65536);
  assert.equal(curated.truncated, true);
  assert.ok(Buffer.byteLength(curated.text, "utf8") <= 65536);
  assert.ok(curated.text.includes("omitted by deterministic evidence limit"));
  assert.ok(curated.text.includes("TAIL_MARKER_"));
});

test("extractVersionInfo preserves malformed versions and detects form-vs-log mismatches", () => {
  const malformed = extractVersionInfo({ "unigetui version": "2016.1" }, "");
  assert.equal(malformed.rawField, "2016.1");
  assert.equal(malformed.era, "malformed-or-unknown");

  const conflict = extractVersionInfo(
    { "unigetui version": "3.1.2-beta0" },
    "Welcome to UniGetUI Version 3.1.1\nLoaded",
  );
  assert.equal(conflict.versionConflict, true);
  assert.equal(conflict.rawLog, "3.1.1");

  const avalonia = extractVersionInfo({ "unigetui version": "2026.3.2" }, "");
  assert.equal(avalonia.era, "current-2026.3");
});

test("detectManagers ignores routine startup which-discovery and count lines in UniGetUI Log", () => {
  const issue = {
    number: 8001,
    title: "[BUG] Window maximize button misaligned",
    body: [
      "### UniGetUI Version",
      "2026.3.2",
      "### Operating system / platform",
      "Windows 11 x64",
      "### Describe your issue",
      "The maximize button is slightly off in the dark theme.",
      "### UniGetUI Log",
      "[10:00:00] Begin \"which\" search for command cargo-binstall.exe",
      "[10:00:01] Found 1 installed packages from PowerShell",
      "[10:00:02] cargo-binstall.exe was not found on the system",
      "### Package Managers Logs",
      "_No response_",
    ].join("\n"),
  };
  const analysis = analyzeIssue(issue);
  assert.deepEqual(analysis.managers, []);
});

test("protected labels are never proposed and humanRemovedLabels blocks re-adding removed labels", () => {
  const issue = {
    number: 9001,
    title: "[BUG] Scoop update fails on Windows 11",
    body: "### UniGetUI Version\n2026.3.2\n### Windows version, edition, and architecture\nWindows 11 x64\n### Describe your issue\nScoop fails to update git.",
    labels: [{ name: "bug" }, { name: "ready-to-go" }],
  };
  const events = [
    { event: "unlabeled", label: { name: "scoop" }, actor: { login: "marticliment", type: "User" } },
  ];
  const analysis = analyzeIssue(issue, [], events);
  assert.ok(!analysis.proposedLabels.includes("scoop"));
  assert.ok(analysis.proposedLabels.includes("platform/windows"));
  for (const label of analysis.proposedLabels) {
    assert.ok(!PROTECTED_LABELS.has(label));
  }

  const fp1 = contentFingerprint(issue, [{ id: 1, user: { login: "author", type: "User" }, body: "extra info" }]);
  const fp2 = contentFingerprint(
    { ...issue, labels: [{ name: "bug" }, { name: "ready-to-go" }, { name: "platform/windows" }] },
    [
      { id: 1, user: { login: "author", type: "User" }, body: "extra info" },
      { id: 2, user: { login: "github-actions[bot]", type: "Bot" }, body: "bot note" },
    ],
  );
  assert.equal(fp1, fp2);
  assert.ok(humanRemovedLabels(events).has("scoop"));
});

test("validation and publication enforce schema, deterministic preservation, and non-destructive advice", () => {
  const issue = {
    number: 101,
    state: "open",
    title: "[BUG] Scoop error",
    body: "### UniGetUI Version\n2026.3.2\n### Windows version, edition, and architecture\nWindows 11 x64\n### Describe your issue\nScoop fails",
    labels: [{ name: "bug" }, { name: "automation/ai-triage" }],
  };
  const fp = contentFingerprint(issue, []);
  const context = {
    number: 101,
    fingerprint: fp,
    existingLabels: ["bug", "automation/ai-triage"],
    removedByHuman: [],
    deterministic: { proposedLabels: ["scoop", "platform/windows"] },
    comments: [{ id: 55, author: "user1", body: "log snippet" }],
    relatedIssues: [{ number: 99 }],
    availableCapabilities: [{ id: "cli-and-tui", sinceRelease: "v2026.3.2" }],
  };

  const validTriage = {
    issueNumber: 101,
    fingerprint: fp,
    abstain: false,
    issueKind: "bug",
    proposedLabels: ["scoop", "platform/windows"],
    ownershipHypothesis: {
      category: "unigetui-integration",
      confidence: "medium",
      basis: "Scoop argument handling",
    },
    evidenceCitations: [{ source: "comment", commentId: 55, quote: "log snippet" }],
    relatedCandidates: [{ number: 99, similarityBasis: "same scoop command", importantDifference: "different version" }],
    capabilityMatches: [],
    missingDiagnostics: ["Please attach the Package Manager log for Scoop."],
    recommendedQueue: "current-regressions",
    summary: "Scoop issue on Windows 11.",
  };
  assert.deepEqual(validate(validTriage, "triage", context), { ok: true });

  const badProtected = {
    ...validTriage,
    proposedLabels: ["scoop", "platform/windows", "ready-to-go"],
  };
  assert.equal(validate(badProtected, "triage", context).ok, false);

  const badAdvice = {
    ...validTriage,
    missingDiagnostics: ["Please delete AppData\\Local\\UniGetUI and retry."],
  };
  assert.equal(validate(badAdvice, "triage", context).ok, false);

  const validVerifier = {
    issueNumber: 101,
    fingerprint: fp,
    verified: true,
    approvedLabels: ["scoop", "platform/windows"],
    rejectedLabels: [],
    summary: "Verified additive labels.",
  };
  assert.deepEqual(validate(validVerifier, "verifier", context, validTriage), { ok: true });

  const report = {
    number: 101,
    fingerprint: fp,
    finalLabels: ["scoop", "platform/windows"],
  };
  const publishPlan = verifyPublicationPlan({
    report,
    liveIssue: issue,
    liveComments: [],
    liveEvents: [],
    env: { ISSUE_AUTOMATION_MODE: "publish-labels" },
  });
  assert.equal(publishPlan.apply, true);
  assert.deepEqual(publishPlan.labelsToAdd, ["scoop", "platform/windows"]);

  const reportOnlyPlan = verifyPublicationPlan({
    report,
    liveIssue: issue,
    liveComments: [],
    liveEvents: [],
    env: { ISSUE_AUTOMATION_MODE: "report-only" },
  });
  assert.equal(reportOnlyPlan.apply, false);
});
