"use strict";

const fs = require("node:fs");
const path = require("node:path");
const capabilitiesManifest = require("./capabilities.json");
const {
  policy,
  isBotUser,
  redactText,
  curateBody,
  analyzeIssue,
} = require("./policy");

const STOP_WORDS = new Set([
  "the", "and", "for", "with", "from", "that", "this", "when", "what", "have", "does", "not",
  "are", "was", "were", "will", "can", "cannot", "cant", "into", "after", "before", "while",
  "unigetui", "wingetui", "bug", "feature", "request", "improvement", "enhancement", "issue",
  "enter", "your", "description", "here", "package", "packages", "version", "update", "updates",
]);

function tokenize(text = "") {
  return [...new Set(
    text
      .toLowerCase()
      .replace(/[^a-z0-9.+-]+/g, " ")
      .split(/\s+/)
      .filter((token) => token.length >= 3 && !STOP_WORDS.has(token) && !/^\d+$/.test(token))
  )];
}

function matchCapabilities(issue, analysis, comments = []) {
  const title = issue.title || "";
  const body = issue.body || "";
  const discussionText = comments.map((c) => c.body || "").join("\n");
  const combined = `${title}\n${body}`;
  const matches = [];

  if (/\b(?:mac\s*os|macos|linux\s+support|cross[- ]platform)\b/i.test(title) ||
      (analysis.issueKind === "feature-or-enhancement" && /\b(?:mac\s*os|macos|linux)\b/i.test(combined))) {
    const cap = capabilitiesManifest.capabilities.find((c) => c.id === "cross-platform-desktop");
    const wantsFlatpak = /\b(?:flatpak|flathub)\b/i.test(combined);
    matches.push({
      capabilityId: cap.id,
      sinceRelease: cap.sinceRelease,
      matchStatus: wantsFlatpak ? "unmet-distinct-scope" : "released-needs-scope-verification",
      shippedScope: cap.shippedScope,
      unmetScope: wantsFlatpak
        ? ["Requests Flatpak/Flathub packaging specifically, which is separate from shipped .deb/.rpm/.tar.gz Linux assets."]
        : cap.unmetOrDistinctScope,
    });
  }

  if (/\b(?:native\s+arm64|arm64\s+version|windows\s+arm64|apple\s+silicon)\b/i.test(combined)) {
    const cap = capabilitiesManifest.capabilities.find((c) => c.id === "native-arm64");
    const hasArchSelectionFollowUp = /\b(?:preferred\s+architecture|overriding.*arm64\s+with\s+x64|x64\s+version)\b/i.test(`${combined}\n${discussionText}`);
    matches.push({
      capabilityId: cap.id,
      sinceRelease: cap.sinceRelease,
      matchStatus: hasArchSelectionFollowUp ? "partial-with-open-followup" : "released-needs-scope-verification",
      shippedScope: cap.shippedScope,
      unmetScope: hasArchSelectionFollowUp
        ? ["Discussion reports per-package ARM64 vs x64 installer selection edge cases in WinGet updates."]
        : cap.unmetOrDistinctScope,
    });
  }

  if (analysis.issueKind === "feature-or-enhancement" &&
      /\b(?:cli\s+support|cli\s+flag|run\s+as\s+cli|--no-ui|headless\s+install)\b/i.test(combined)) {
    const cliCap = capabilitiesManifest.capabilities.find((c) => c.id === "cli-and-tui");
    const headlessCap = capabilitiesManifest.capabilities.find((c) => c.id === "headless-daemon");
    const wantsGranularHeadlessFlags = /\b(?:always\s+elevate|cache\s+administrator\s+rights|disable\s+any\s+possible\s+uac)\b/i.test(combined);
    const wantsSingleShotBundleFlag = /--install\s+--no-ui/i.test(combined);
    matches.push({
      capabilityId: wantsGranularHeadlessFlags ? headlessCap.id : cliCap.id,
      sinceRelease: cliCap.sinceRelease,
      matchStatus: wantsGranularHeadlessFlags || wantsSingleShotBundleFlag
        ? "partial-with-unmet-acceptance-points"
        : "released-needs-scope-verification",
      shippedScope: [...cliCap.shippedScope, ...headlessCap.shippedScope],
      unmetScope: wantsGranularHeadlessFlags
        ? headlessCap.unmetOrDistinctScope
        : wantsSingleShotBundleFlag
          ? ["Shipped CLI uses 'uniget bundle import' and 'uniget bundle install' against a session rather than 'UniGetUI file --install --no-ui'."]
          : cliCap.unmetOrDistinctScope,
    });
  }

  if (analysis.issueKind === "feature-or-enhancement" &&
      /\b(?:schedule[d]?\s+install\s+time|monthly|1x\s*\/\s*monthly|schedule.*update)\b/i.test(combined)) {
    const cap = capabilitiesManifest.capabilities.find((c) => c.id === "scheduled-maintenance");
    const wantsMonthly = /\b(?:monthly|1x\s*\/\s*monthly)\b/i.test(combined);
    matches.push({
      capabilityId: cap.id,
      sinceRelease: cap.sinceRelease,
      matchStatus: wantsMonthly ? "partial-with-unmet-acceptance-points" : "released-needs-scope-verification",
      shippedScope: cap.shippedScope,
      unmetScope: wantsMonthly
        ? ["Current scheduler supports interval, daily, and weekly triggers, not a monthly trigger."]
        : cap.unmetOrDistinctScope,
    });
  }

  return matches;
}

function analyzeDiscussionState(issue, analysis, comments = [], events = []) {
  const existing = new Set(analysis.existingLabels);
  const nonBot = comments.filter((c) => !isBotUser(c.user || c.author));
  const lastComment = nonBot.at(-1) || null;
  const lastCommentAuthor = lastComment?.user?.login || lastComment?.author?.login || null;
  const issueAuthor = analysis.author;

  if (!analysis.evidenceBounds.commentsComplete) {
    return {
      authorReplied: false,
      releaseVerificationNeeded: false,
      chronologyVerified: false,
      evidenceIncomplete: true,
      note: "Comment history was not completely loaded; defer next-actor state transitions.",
    };
  }

  let waitingLabelTime = null;
  for (const event of events) {
    if (event.event === "labeled" &&
        ["needs-author-answer", "waiting-for-user-feedback"].includes(event.label?.name) &&
        event.created_at) {
      const ts = Date.parse(event.created_at);
      if (!Number.isNaN(ts) && (waitingLabelTime === null || ts > waitingLabelTime)) {
        waitingLabelTime = ts;
      }
    }
  }

  const hasWaitingLabel = existing.has("needs-author-answer") || existing.has("waiting-for-user-feedback");
  let authorReplied = false;
  let chronologyVerified = false;
  if (hasWaitingLabel && lastComment && lastCommentAuthor && lastCommentAuthor === issueAuthor) {
    const commentTime = Date.parse(lastComment.created_at || lastComment.createdAt || "");
    if (waitingLabelTime !== null && !Number.isNaN(commentTime)) {
      authorReplied = commentTime >= waitingLabelTime;
      chronologyVerified = true;
    } else {
      authorReplied = true;
      chronologyVerified = false;
    }
  }

  const hasReleaseLabel = existing.has("released") || existing.has("ready-for-release");
  let releaseVerificationNeeded = false;
  let releaseFollowUpReason = null;
  if (hasReleaseLabel) {
    const recentBodies = nonBot.slice(-3).map((c) => c.body || "").join("\n");
    if (/\b(?:bug\s+persists|still\s+exists|still\s+have\s+this\s+issue|also\s+seeing\s+this|not\s+shown\s+in\s+the\s+installed|odd\s+behavior|flickering|keeps\s+trying\s+to\s+install|need\s+to\s+investigate\s+why)\b/i.test(recentBodies)) {
      releaseVerificationNeeded = true;
      releaseFollowUpReason = "Recent discussion after release/fix tagging reports residual behavior, ongoing reproduction, or open maintainer investigation.";
    } else if (!lastComment) {
      releaseVerificationNeeded = true;
      releaseFollowUpReason = "Issue carries a release-state label without user confirmation in comments.";
    }
  }

  return {
    authorReplied,
    chronologyVerified,
    releaseVerificationNeeded,
    releaseFollowUpReason,
    evidenceIncomplete: false,
    lastCommentAuthor,
    lastCommentId: lastComment?.id || null,
  };
}

function matchOpenPullRequests(issue, analysis, openPrs = []) {
  const candidates = [];
  const issueTokens = new Set(tokenize(`${issue.title || ""}\n${issue.body || ""}`));
  for (const pr of openPrs) {
    const prText = `${pr.title || ""}\n${pr.body || ""}`;
    const explicitRef = new RegExp(`(?:^|\\s|#|/)${issue.number}\\b`).test(prText);
    const prTokens = tokenize(prText);
    const shared = prTokens.filter((t) => issueTokens.has(t));
    const headlessElevatorMatch =
      /\bheadless\b/i.test(issue.title || "") && /\belevat/i.test(`${issue.title}\n${issue.body}`) &&
      /\bheadless\b/i.test(pr.title || "") && /\belevator\b/i.test(pr.title || "");
    const tabFocusMatch =
      /\b(?:tab|focus|keyboard)\b/i.test(issue.title || "") &&
      /\b(?:tab|shift\+tab|traversal)\b/i.test(pr.title || "");
    if (explicitRef || headlessElevatorMatch || tabFocusMatch || shared.length >= 4) {
      candidates.push({
        number: pr.number,
        title: pr.title,
        matchBasis: explicitRef
          ? "explicit-issue-reference"
          : headlessElevatorMatch || tabFocusMatch
            ? "component-and-symptom-match"
            : `shared-tokens:${shared.slice(0, 4).join(",")}`,
      });
    }
  }
  return candidates.slice(0, 5);
}

function findRelatedIssues(target, allRecords = []) {
  const targetTokens = new Set(tokenize(target.analysis.title));
  const scored = [];
  for (const candidate of allRecords) {
    if (candidate.analysis.number === target.analysis.number) continue;
    const sharedSignals = [];
    let score = 0;

    for (const pkg of target.analysis.packageIds) {
      if (candidate.analysis.packageIds.includes(pkg)) {
        score += 5;
        sharedSignals.push(`package:${pkg}`);
      }
    }
    for (const code of target.analysis.errorCodes) {
      if (candidate.analysis.errorCodes.includes(code)) {
        score += 4;
        sharedSignals.push(`error:${code}`);
      }
    }
    for (const mgr of target.analysis.managers) {
      if (candidate.analysis.managers.includes(mgr)) {
        score += 2;
        sharedSignals.push(`manager:${mgr}`);
      }
    }
    for (const area of target.analysis.areas) {
      if (candidate.analysis.areas.includes(area)) {
        score += 2;
        sharedSignals.push(area);
      }
    }
    const candidateTokens = tokenize(candidate.analysis.title);
    const commonTokens = candidateTokens.filter((t) => targetTokens.has(t));
    if (commonTokens.length >= 2) {
      score += Math.min(6, commonTokens.length * 2);
      sharedSignals.push(`terms:${commonTokens.slice(0, 4).join(",")}`);
    }

    if (score < 4) continue;

    const distinguishingFactors = [];
    if (target.analysis.versionInfo.era !== candidate.analysis.versionInfo.era) {
      distinguishingFactors.push(`era:${target.analysis.versionInfo.era} vs ${candidate.analysis.versionInfo.era}`);
    }
    if (target.analysis.backend !== candidate.analysis.backend &&
        (target.analysis.backend !== "unknown" || candidate.analysis.backend !== "unknown")) {
      distinguishingFactors.push(`backend:${target.analysis.backend} vs ${candidate.analysis.backend}`);
    }
    if (target.analysis.issueKind !== candidate.analysis.issueKind) {
      distinguishingFactors.push(`kind:${target.analysis.issueKind} vs ${candidate.analysis.issueKind}`);
    }
    if (distinguishingFactors.length === 0) {
      distinguishingFactors.push("Verify exact reproduction steps, scope, and arguments before treating as duplicate.");
    }

    scored.push({
      number: candidate.analysis.number,
      title: candidate.analysis.title,
      score,
      sharedSignals,
      distinguishingFactors,
    });
  }
  return scored
    .sort((a, b) => b.score - a.score || b.number - a.number)
    .slice(0, policy.maxRelatedCandidates);
}

function buildIssueRecord(issue, { comments = [], events = [], openPrs = [] } = {}) {
  const analysis = analyzeIssue(issue, comments, events);
  const capabilityMatches = matchCapabilities(issue, analysis, comments);
  const discussionState = analyzeDiscussionState(issue, analysis, comments, events);
  const candidatePrs = matchOpenPullRequests(issue, analysis, openPrs);
  return {
    issue,
    analysis,
    capabilityMatches,
    discussionState,
    candidatePrs,
    relatedIssues: [],
  };
}

function enrichBatchRecords(records) {
  for (const record of records) {
    record.relatedIssues = findRelatedIssues(record, records);
  }
  return records;
}

function buildDigest(records, { generatedAt = new Date().toISOString(), mode = "report-only", aiEnabled = false } = {}) {
  enrichBatchRecords(records);
  const queues = {
    securityEscalations: [],
    currentRegressions: [],
    authorReplied: [],
    releaseVerification: [],
    implementedOrPartial: [],
    relatedClusters: [],
    informationGaps: [],
    upstreamOrEnvironment: [],
    roadmapRequests: [],
    incompleteEvidence: [],
    modelFailed: [],
  };

  for (const record of records) {
    const { analysis, capabilityMatches, discussionState, candidatePrs, relatedIssues } = record;
    const entry = {
      number: analysis.number,
      title: analysis.title,
      fingerprint: analysis.fingerprint,
      version: analysis.versionInfo.version,
      era: analysis.versionInfo.era,
      existingLabels: analysis.existingLabels,
      labelsToAdd: analysis.labelsToAdd,
      protectedLabels: analysis.protectedLabels,
      managers: analysis.managers,
      areas: analysis.areas,
      platform: analysis.platform.labels,
      candidatePrs,
      relatedIssues,
      capabilityMatches,
      discussionState,
      ownershipHypothesis: analysis.ownershipHypothesis,
      missingDiagnostics: analysis.missingDiagnostics,
      destructiveRisk: analysis.destructiveRisk,
    };

    if (record.modelStatus === "failed") {
      queues.modelFailed.push({ ...entry, failureReason: record.modelFailureReason || "AI stage failed" });
    }
    if (analysis.security.sensitive) {
      queues.securityEscalations.push({
        ...entry,
        recommendedAction: `Review privately; direct vulnerability disclosures to ${analysis.security.reportingUrl} without echoing sensitive details.`,
      });
      continue;
    }
    if (discussionState.evidenceIncomplete || analysis.evidenceBounds.bodyTruncated) {
      queues.incompleteEvidence.push({
        ...entry,
        recommendedAction: "Hydrate full issue discussion or omitted log sections before taking destructive or state-changing action.",
      });
    }
    if (discussionState.authorReplied) {
      queues.authorReplied.push({
        ...entry,
        recommendedAction: discussionState.chronologyVerified
          ? "Original author replied after waiting label was applied; review response and update human workflow state."
          : "Original author is the latest commenter on a waiting-labeled issue; verify label event timestamp and review reply.",
      });
    }
    if (discussionState.releaseVerificationNeeded) {
      queues.releaseVerification.push({
        ...entry,
        recommendedAction: discussionState.releaseFollowUpReason,
      });
    }
    if (capabilityMatches.length > 0) {
      queues.implementedOrPartial.push({
        ...entry,
        recommendedAction: "Compare shipped v2026.3.2 capability against unmet acceptance points before posting a status update; do not auto-close.",
      });
    }
    if (analysis.issueKind === "bug" &&
        (analysis.versionInfo.era === "current-2026.3" || analysis.destructiveRisk || candidatePrs.length > 0)) {
      queues.currentRegressions.push({
        ...entry,
        recommendedAction: candidatePrs.length > 0
          ? `Verify current regression and candidate fix PR(s): ${candidatePrs.map((p) => `#${p.number}`).join(", ")}.`
          : "Triage current-release or high-impact defect report.",
      });
    }
    if (relatedIssues.length > 0) {
      queues.relatedClusters.push({
        ...entry,
        recommendedAction: `Review related issue candidates (${relatedIssues.map((r) => `#${r.number}`).join(", ")}); preserve backend/scope differences.`,
      });
    }
    if (analysis.missingDiagnostics.length > 0 && !discussionState.authorReplied) {
      queues.informationGaps.push({
        ...entry,
        recommendedAction: `If needed for diagnosis, ask focused question(s): ${analysis.missingDiagnostics.map((d) => d.field).join(", ")}.`,
      });
    }
    if (analysis.ownershipHypothesis.category === "manager-upstream" ||
        analysis.ownershipHypothesis.category === "package-manifest-or-installer" ||
        (analysis.commandComparison.hasComparison && analysis.commandComparison.equivalent === false)) {
      queues.upstreamOrEnvironment.push({
        ...entry,
        recommendedAction: analysis.commandComparison.equivalent === false
          ? analysis.commandComparison.note
          : `${analysis.ownershipHypothesis.basis} Confirm with equivalent terminal command before applying 'not-UniGetUI'.`,
      });
    }
    if (analysis.issueKind === "feature-or-enhancement" && capabilityMatches.length === 0) {
      queues.roadmapRequests.push({
        ...entry,
        recommendedAction: "Human roadmap prioritization; apply additive area/manager labels only.",
      });
    }
  }

  return {
    version: 1,
    generatedAt,
    mode,
    aiEnabled,
    pinnedRelease: capabilitiesManifest.pinnedRelease.tag,
    totalAnalyzed: records.length,
    summaryCounts: Object.fromEntries(Object.entries(queues).map(([k, v]) => [k, v.length])),
    queues,
  };
}

function renderDigestMarkdown(digest) {
  const lines = [
    "## UniGetUI Issue Intelligence Digest",
    "",
    `- **Mode:** \`${digest.mode}\` (AI enabled: \`${digest.aiEnabled}\`)`,
    `- **Pinned capability release:** \`${digest.pinnedRelease}\``,
    `- **Total issues analyzed in batch:** ${digest.totalAnalyzed}`,
    "",
    "| Queue | Count | Purpose |",
    "| --- | ---: | --- |",
    `| Current regressions & impact | ${digest.summaryCounts.currentRegressions} | Current ${policy.currentReleaseSeries} bugs, destructive impact, or candidate fix PRs |`,
    `| Author replied | ${digest.summaryCounts.authorReplied} | Waiting-labeled issues where the reporter is the latest commenter |`,
    `| Release verification | ${digest.summaryCounts.releaseVerification} | \`released\` / \`ready-for-release\` issues with unresolved follow-up |`,
    `| Shipped or partial capability | ${digest.summaryCounts.implementedOrPartial} | Requests matching shipped ${digest.pinnedRelease} capabilities with scope notes |`,
    `| Related clusters | ${digest.summaryCounts.relatedClusters} | Evidence-linked related issues (never auto-closed) |`,
    `| Focused information gaps | ${digest.summaryCounts.informationGaps} | Task-specific missing diagnostic fields |`,
    `| Upstream / command comparison | ${digest.summaryCounts.upstreamOrEnvironment} | Upstream hypotheses or non-equivalent command comparisons |`,
    `| Roadmap requests | ${digest.summaryCounts.roadmapRequests} | Unmet feature and enhancement proposals |`,
    `| Incomplete evidence / truncated | ${digest.summaryCounts.incompleteEvidence} | Large logs or partial discussion requiring hydration |`,
    `| Model failed | ${digest.summaryCounts.modelFailed} | AI stage failures (tracked separately from clean triage) |`,
    "",
  ];

  const sections = [
    ["Current regressions & impact", digest.queues.currentRegressions],
    ["Author replied (needs maintainer review)", digest.queues.authorReplied],
    ["Release verification needed", digest.queues.releaseVerification],
    ["Shipped or partially fulfilled requests", digest.queues.implementedOrPartial],
    ["Upstream hypotheses & command-equivalence checks", digest.queues.upstreamOrEnvironment],
  ];

  for (const [heading, items] of sections) {
    if (!items.length) continue;
    lines.push(`### ${heading}`, "");
    for (const item of items.slice(0, 15)) {
      const addLabels = item.labelsToAdd.length ? ` | +labels: \`${item.labelsToAdd.join("`, `")}\`` : "";
      const prs = item.candidatePrs.length ? ` | candidate PRs: ${item.candidatePrs.map((p) => `#${p.number}`).join(", ")}` : "";
      lines.push(`- **#${item.number}** (${item.version || item.era}): ${item.title}${addLabels}${prs}`);
      lines.push(`  - Action: ${item.recommendedAction}`);
    }
    lines.push("");
  }

  lines.push(
    "Automated issue intelligence is advisory. Human workflow labels (`ready-to-go`, `needs-further-study`, `needs-author-answer`, `released`, `duplicate`, `not-UniGetUI`) are preserved and never overwritten."
  );
  return lines.join("\n");
}

function prepareIssueContext(record, automationSha, root = process.cwd()) {
  const { issue, analysis, capabilityMatches, discussionState, candidatePrs, relatedIssues } = record;
  const curated = curateBody(issue.body || "", policy.maxBodyBytes);
  const nonBotComments = (record.comments || [])
    .filter((c) => !isBotUser(c.user || c.author))
    .slice(-20)
    .map((c) => ({
      id: c.id,
      author: c.user?.login || c.author?.login || null,
      createdAt: c.created_at || c.createdAt || null,
      body: redactText((c.body || "").slice(0, 4000)),
    }));

  const context = {
    version: 1,
    number: analysis.number,
    fingerprint: analysis.fingerprint,
    automationSha,
    title: analysis.title,
    body: curated.text,
    bodyTruncated: curated.truncated,
    omittedBytes: curated.omittedBytes,
    existingLabels: analysis.existingLabels,
    protectedLabels: analysis.protectedLabels,
    removedByHuman: analysis.removedByHuman,
    deterministic: {
      issueKind: analysis.issueKind,
      proposedLabels: analysis.proposedLabels,
      labelsToAdd: analysis.labelsToAdd,
      versionInfo: analysis.versionInfo,
      platform: analysis.platform,
      managers: analysis.managers,
      backend: analysis.backend,
      channel: analysis.channel,
      areas: analysis.areas,
      errorCodes: analysis.errorCodes,
      packageIds: analysis.packageIds,
      commandComparison: analysis.commandComparison,
      ownershipHypothesis: analysis.ownershipHypothesis,
      missingDiagnostics: analysis.missingDiagnostics,
    },
    discussionState,
    capabilityMatches,
    candidatePrs,
    relatedIssues,
    comments: nonBotComments,
    pinnedRelease: capabilitiesManifest.pinnedRelease.tag,
    availableCapabilities: capabilitiesManifest.capabilities.map((c) => ({
      id: c.id,
      status: c.status,
      sinceRelease: c.sinceRelease,
    })),
  };

  if (Buffer.byteLength(JSON.stringify(context), "utf8") > policy.maxBundleBytes) {
    throw new Error("Curated issue context bundle exceeds maxBundleBytes");
  }

  const evidenceDir = path.join(root, "issue-evidence");
  fs.mkdirSync(evidenceDir, { recursive: true });
  fs.writeFileSync(path.join(evidenceDir, `context-${analysis.number}.json`), JSON.stringify(context));
  fs.writeFileSync(path.join(evidenceDir, "context.json"), JSON.stringify(context));
  return context;
}

module.exports = {
  tokenize,
  matchCapabilities,
  analyzeDiscussionState,
  matchOpenPullRequests,
  findRelatedIssues,
  buildIssueRecord,
  enrichBatchRecords,
  buildDigest,
  renderDigestMarkdown,
  prepareIssueContext,
};
