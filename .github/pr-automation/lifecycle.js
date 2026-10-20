"use strict";

const { policy, SHA } = require("./policy");
const { escape } = require("./validation");
const STATE_MARKER = "<!-- unigetui-pr-automation-state:v1 -->";
const REVIEW_PATTERN = /^<!-- unigetui-pr-review:v1 head=([a-f0-9]{40}) base=([a-f0-9]{40}) automation=([a-f0-9]{40}) risk=(low|medium|high|unknown) legitimacy=([01]) overlap=([01]) action=([01]) -->/;
const BOT = "github-actions[bot]";

function reviewMarker(snapshot, report) {
  if (![snapshot.head, snapshot.base, snapshot.automationSha].every((value) => SHA.test(value))) {
    throw new Error("Invalid review provenance");
  }
  const action = report.review.findings.some((finding) => ["critical", "high", "medium"].includes(finding.severity));
  return `<!-- unigetui-pr-review:v1 head=${snapshot.head} base=${snapshot.base} automation=${snapshot.automationSha} ` +
    `risk=${report.classifier.risk} legitimacy=${Number(report.classifier.legitimacy)} ` +
    `overlap=${Number(report.classifier.overlap.length > 0)} action=${Number(action)} -->`;
}

function published(reviews) {
  return reviews.filter((review) => review.user?.login === BOT && review.user.type === "Bot" &&
    review.state === "COMMENTED" && REVIEW_PATTERN.test(review.body || "") &&
    REVIEW_PATTERN.exec(review.body)[1] === review.commit_id).map((review) => {
    const [, head, base, automationSha, risk, legitimacy, overlap, action] = REVIEW_PATTERN.exec(review.body);
    return { head, base, automationSha, review, risk, legitimacy: legitimacy === "1", overlap: overlap === "1", action: action === "1" };
  });
}

function reviewGate(reviews, head) {
  const prior = published(reviews);
  return { duplicate: prior.some((entry) => entry.head === head),
    count: new Set(prior.map((entry) => entry.head)).size };
}

function authorAction(reviews) {
  const latest = new Map();
  for (const review of [...reviews].sort((a, b) => a.id - b.id)) {
    if (review.user?.type !== "Bot" && ["APPROVED", "CHANGES_REQUESTED", "DISMISSED"].includes(review.state)) {
      latest.set(review.user.login, review.state);
    }
  }
  return [...latest.values()].includes("CHANGES_REQUESTED");
}

function desiredLabels(snapshot, { report, failed = false, reviews = [], closed = false } = {}) {
  const labels = [...snapshot.analysis.labels];
  const previous = published(reviews).filter((entry) => entry.head === snapshot.head && entry.base === snapshot.base).at(-1);
  const count = Math.min(policy.maxReviews, reviewGate(reviews, snapshot.head).count);
  if (count) labels.push(`ai-reviewed/${count}`);
  labels.push(`risk/${report?.classifier?.risk || previous?.risk || "unknown"}`);
  if (report?.classifier.legitimacy || previous?.legitimacy) labels.push("triage/legitimacy");
  if (report?.classifier.overlap.length || previous?.overlap) labels.push("triage/overlap");
  if (!closed) {
    if (failed) labels.push("automation-failed");
    const action = authorAction(reviews) || previous?.action ||
      report?.review.findings.some((finding) => ["critical", "high", "medium"].includes(finding.severity));
    labels.push(action ? "needs-author-action" : "needs-review");
  }
  return labels;
}

function stateComment(comments) {
  return comments.filter((comment) => comment.user?.login === BOT && comment.user.type === "Bot" &&
    comment.body?.startsWith(STATE_MARKER)).sort((a, b) => b.id - a.id)[0];
}

function readLease(comment, now) {
  if (!comment) return null;
  const match = /^<!-- unigetui-pr-automation-state:v1 -->\n<!-- lease:([a-f0-9]{40}):(\d+):(\d+) -->/.exec(comment.body);
  if (!match) throw new Error("Existing automation lease is malformed");
  const expires = Number(match[3]);
  if (!Number.isSafeInteger(expires) || expires > now + 15 * 60 * 1000) throw new Error("Invalid automation lease expiry");
  return { head: match[1], run: match[2], expires };
}

function stateBody(snapshot, now, message) {
  if (!/^\d+$/.test(snapshot.runId)) throw new Error("Invalid lease owner");
  return `${STATE_MARKER}\n<!-- lease:${snapshot.head}:${snapshot.runId}:${now} -->\n` +
    `PR automation for head \`${snapshot.head}\`.\n\n${escape(message)}\n\n` +
    "This is advisory automation state, not approval. Human review remains required.";
}

module.exports = { STATE_MARKER, BOT, published, reviewMarker, reviewGate, authorAction, desiredLabels,
  stateComment, readLease, stateBody };
