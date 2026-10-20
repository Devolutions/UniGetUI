"use strict";

const fs = require("node:fs");
const { validateFinalReview } = require("./validate-final-review");

function load(file) {
  return JSON.parse(fs.readFileSync(file, "utf8"));
}

function preserve(review, { previousCandidate }) {
  if (previousCandidate?.findings.length && !review.findings.length) {
    return { ok: false, reason: "restore the missing findings" };
  }
  return null;
}

function validateSpecialist(candidate, context) {
  const lost = preserve(candidate, context);
  if (lost) return lost;
  const changed = load(context.metadata.validation_context_file).changed_paths;
  for (let index = 0; index < candidate.findings.length; index++) {
    if (!changed.includes(candidate.findings[index].path)) {
      return { ok: false, reason: `finding at index ${index} must cite a path changed in this review` };
    }
  }
  return { ok: true };
}

function validateGeneral(candidate, context) {
  const lost = preserve(candidate, context);
  if (lost) return lost;
  return validateFinalReview(candidate, {
    expectedSha: context.metadata.expected_sha,
    changedPaths: load(context.metadata.validation_context_file).changed_paths,
    specialistAggregate: load(context.metadata.aggregate_file),
  });
}

module.exports = { validateGeneral, validateSpecialist };
