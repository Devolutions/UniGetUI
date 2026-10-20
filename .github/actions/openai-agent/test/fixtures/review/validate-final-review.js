"use strict";

// Test-only semantic validator: isolates runtime repair/diagnostic behavior from repository policy.
function key(value) {
  return `${value.reviewer}:${value.finding_id}`;
}

function validateFinalReview(raw, { expectedSha, changedPaths, specialistAggregate }) {
  const review = typeof raw === "string" ? JSON.parse(raw) : raw;
  if (review.head_sha !== expectedSha) return { ok: false, reason: "wrong head SHA" };
  for (let index = 0; index < review.findings.length; index++) {
    if (!changedPaths.includes(review.findings[index].path)) {
      return { ok: false, reason: `finding at index ${index} must cite a path changed in this review` };
    }
  }
  const candidates = specialistAggregate.reviewers.filter((entry) => entry.status === "valid")
    .flatMap((entry) => entry.findings.map((finding, index) => ({
      reviewer: entry.reviewer, finding_id: finding.id, index,
    })));
  const known = new Map(candidates.map((candidate) => [key(candidate), candidate]));
  const seen = new Set();
  const covered = new Set();
  let duplicates = 0;
  let unknown = 0;
  let invalidRationale = 0;
  let controls = 0;
  for (const entry of review.candidate_dispositions) {
    const identity = key(entry);
    if (seen.has(identity)) duplicates++;
    seen.add(identity);
    if (!known.has(identity)) unknown++;
    const control = /[\u0000-\u001f]/.test(entry.rationale);
    if (control) controls++;
    if (!entry.rationale.trim() || control) invalidRationale++;
    else if (known.has(identity)) covered.add(identity);
  }
  const missing = candidates.filter((candidate) => !covered.has(key(candidate)));
  const reasons = [];
  if (missing.length) reasons.push(`${missing.length} of ${candidates.length} candidates have no valid disposition`);
  if (duplicates) reasons.push(`${duplicates} duplicate entries`);
  if (unknown) reasons.push(`${unknown} unknown entries`);
  if (invalidRationale) reasons.push(`${invalidRationale} with a missing or invalid rationale`);
  if (controls) reasons.push(`${controls} forbidden-control entries`);
  if (reasons.length) {
    const reason = reasons.join("; ");
    const coordinates = missing.map((candidate, index) =>
      index > 0 && missing[index - 1].reviewer === candidate.reviewer
        ? candidate.index : `${candidate.reviewer} ${candidate.index}`).join(", ");
    return {
      ok: false, reason, detail: reason,
      guidance: missing.length
        ? `aggregate findings ${coordinates}: ` +
          missing.map((candidate) => `(${candidate.reviewer}, ${candidate.finding_id})`).join(", ")
        : undefined,
    };
  }
  return { ok: true, value: { head_sha: review.head_sha, summary: review.summary, findings: review.findings } };
}

function provenancePrefix(sources) {
  return `[${[...new Set(sources.map((source) => source.reviewer))].join(", ")}]`;
}

module.exports = { validateFinalReview, provenancePrefix };
