"use strict";

const fs = require("node:fs");
const Ajv = require("ajv");
const { FINGERPRINT, OWNED_LABELS, PROTECTED_LABELS } = require("./policy");

const ajv = new Ajv({ allErrors: true, strict: true });
const schemas = {
  triage: ajv.compile(require("./schemas/triage.json")),
  verifier: ajv.compile(require("./schemas/verifier.json")),
};

const DESTRUCTIVE_ADVICE = /\b(?:delete.*appdata|rm\s+-rf|rmdir.*unigetui|disable\s+(?:antivirus|defender)|--ignore-security-hash)\b/i;

function validate(value, stage, context, triageOutput = null) {
  const schema = schemas[stage];
  if (!schema) throw new Error("Unknown issue validation stage");
  if (!schema(value)) return { ok: false, reason: "output does not match the stage schema" };
  if (!context || !Number.isSafeInteger(context.number) || !FINGERPRINT.test(context.fingerprint)) {
    throw new Error("Invalid trusted issue validation context");
  }
  if (value.issueNumber !== context.number || value.fingerprint !== context.fingerprint) {
    return { ok: false, reason: "issueNumber or fingerprint does not match expected context" };
  }

  const removed = new Set(context.removedByHuman || []);
  const deterministicLabels = (context.deterministic?.proposedLabels || []).filter((l) => !removed.has(l));

  if (stage === "triage") {
    for (const label of value.proposedLabels) {
      if (!OWNED_LABELS.has(label) || PROTECTED_LABELS.has(label) || removed.has(label)) {
        return { ok: false, reason: `proposed label '${label}' is unowned, protected, or removed by a human` };
      }
    }
    for (const label of deterministicLabels) {
      if (!value.proposedLabels.includes(label)) {
        return { ok: false, reason: `deterministic label '${label}' must be preserved` };
      }
    }
    if (value.abstain && value.proposedLabels.length !== deterministicLabels.length) {
      return { ok: false, reason: "abstaining triage output must not add non-deterministic labels" };
    }
    const commentIds = new Set((context.comments || []).map((c) => c.id));
    for (const citation of value.evidenceCitations) {
      if (citation.source === "comment") {
        if (citation.commentId === null || !commentIds.has(citation.commentId)) {
          return { ok: false, reason: "comment citation references an unavailable commentId" };
        }
      } else if (citation.commentId !== null) {
        return { ok: false, reason: "title/body citation must have commentId: null" };
      }
    }
    const candidateNumbers = new Set((context.relatedIssues || []).map((r) => r.number));
    for (const candidate of value.relatedCandidates) {
      if (!candidateNumbers.has(candidate.number)) {
        return { ok: false, reason: "relatedCandidate references an issue outside context.relatedIssues" };
      }
    }
    const capabilities = new Map((context.availableCapabilities || []).map((c) => [c.id, c]));
    for (const match of value.capabilityMatches) {
      const cap = capabilities.get(match.capabilityId);
      if (!cap || cap.sinceRelease !== match.sinceRelease) {
        return { ok: false, reason: "capabilityMatch references an unknown capabilityId or release tag" };
      }
    }
    if (value.missingDiagnostics.some((item) => DESTRUCTIVE_ADVICE.test(item)) ||
        DESTRUCTIVE_ADVICE.test(value.summary)) {
      return { ok: false, reason: "triage output contains prohibited destructive troubleshooting advice" };
    }
    return { ok: true };
  }

  if (!triageOutput || !validate(triageOutput, "triage", context).ok) {
    throw new Error("Invalid trusted triage input for verifier stage");
  }
  const proposedSet = new Set(triageOutput.proposedLabels);
  const approvedSet = new Set(value.approvedLabels);
  const rejectedSet = new Set(value.rejectedLabels);
  if ([...approvedSet].some((l) => rejectedSet.has(l)) ||
      approvedSet.size + rejectedSet.size !== proposedSet.size ||
      [...proposedSet].some((l) => !approvedSet.has(l) && !rejectedSet.has(l))) {
    return { ok: false, reason: "verifier must partition triage.proposedLabels into approvedLabels and rejectedLabels" };
  }
  for (const label of deterministicLabels) {
    if (!approvedSet.has(label)) {
      return { ok: false, reason: `verifier cannot reject deterministic label '${label}'` };
    }
  }
  if (DESTRUCTIVE_ADVICE.test(value.summary)) {
    return { ok: false, reason: "verifier summary contains prohibited destructive advice" };
  }
  return { ok: true };
}

function escape(value) {
  return String(value || "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/([\\`*_[\]{}()#!|~])/g, "\\$1")
    .replace(/@/g, "&#64;");
}

function renderIssueReport(report) {
  const labels = (report.finalLabels || report.triage?.proposedLabels || []).map((l) => `\`${escape(l)}\``).join(", ") || "none";
  const summary = escape(report.verifier?.summary || report.triage?.summary || "Deterministic triage completed.");
  return `### Issue #${report.number} Advisory Triage\n\n` +
    `- **Fingerprint:** \`${report.fingerprint}\`\n` +
    `- **Proposed additive labels:** ${labels}\n\n` +
    `${summary}\n`;
}

function readJson(file) {
  const stat = fs.lstatSync(file);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 8 * 1024 * 1024) {
    throw new Error("Invalid trusted JSON input");
  }
  return JSON.parse(fs.readFileSync(file, "utf8"));
}

function agentValidator(value, { metadata }) {
  let context;
  let triageOutput = null;
  try {
    context = readJson(metadata.context_file);
    if (metadata.stage === "verifier") {
      triageOutput = readJson(metadata.triage_file || "issue-evidence/triage.json");
    }
  } catch {
    const error = new Error("trusted issue validation input unavailable");
    error.code = "VALIDATOR_TERMINAL";
    throw error;
  }
  return validate(value, metadata.stage, context, triageOutput);
}

module.exports = {
  validate,
  escape,
  renderIssueReport,
  readJson,
  agentValidator,
};
