"use strict";

const fs = require("node:fs");
const Ajv = require("ajv");
const { SHA, REVIEWERS, safePath } = require("./policy");
const ajv = new Ajv({ allErrors: true, strict: true });
const schemas = {
  classifier: ajv.compile(require("./schemas/classifier.json")),
  review: ajv.compile(require("./schemas/review.json")),
};

function validate(value, stage, context, aggregate = []) {
  const schema = schemas[stage === "classifier" ? "classifier" : "review"];
  if (!schema(value)) return { ok: false, reason: "output does not match the stage schema" };
  if (!SHA.test(context.head) || !Array.isArray(context.files)) throw new Error("Invalid trusted validation context");
  if (value.head !== context.head) return { ok: false, reason: "head does not match the expected SHA" };
  if (stage === "classifier") {
    if (value.overlap.some((number) => !context.overlap.some((pr) => pr.number === number))) {
      return { ok: false, reason: "overlap references an unavailable pull request" };
    }
    return { ok: true };
  }
  if (value.reviewer !== stage || (stage !== "general" && !REVIEWERS.includes(stage))) {
    return { ok: false, reason: "reviewer does not match the expected stage" };
  }
  const candidates = new Map();
  const stages = new Set();
  for (const output of aggregate) {
    if (!REVIEWERS.includes(output.reviewer) || stages.has(output.reviewer)) throw new Error("Invalid trusted specialist identity");
    stages.add(output.reviewer);
    const valid = validate(output, output.reviewer, context);
    if (!valid.ok) throw new Error("Invalid trusted specialist aggregate");
    for (const item of output.findings) candidates.set(`${output.reviewer}:${item.id}`, item);
  }
  const dispositions = new Map(value.dispositions.map((item) => [item.candidate, item]));
  if (stage === "general") {
    if (dispositions.size !== value.dispositions.length || dispositions.size !== candidates.size ||
        [...dispositions.keys()].some((key) => !candidates.has(key))) {
      return { ok: false, reason: "general review must dispose of every candidate exactly once" };
    }
  } else if (value.dispositions.length !== 0) return { ok: false, reason: "specialist dispositions must be empty" };
  const ids = new Set();
  const used = new Set();
  for (const finding of value.findings) {
    const file = context.files.find((entry) => entry.filename === finding.path);
    if (!safePath(finding.path) || !file || ids.has(finding.id) ||
        (finding.line !== null && !file.addedLines.includes(finding.line))) {
      return { ok: false, reason: "finding must have a unique ID and a changed path and added line" };
    }
    ids.add(finding.id);
    if (stage !== "general" && finding.sources.length) return { ok: false, reason: "specialists cannot claim other sources" };
    for (const source of finding.sources) {
      if (!candidates.has(source) || dispositions.get(source)?.decision === "rejected" || used.has(source)) {
        return { ok: false, reason: "finding provenance is invalid or repeated" };
      }
      used.add(source);
    }
  }
  if ([...dispositions.values()].some((entry) => entry.decision !== "rejected" && !used.has(entry.candidate))) {
    return { ok: false, reason: "accepted and refined candidates must appear in the final findings" };
  }
  if (Buffer.byteLength(JSON.stringify(value)) > 256 * 1024 || render(value).length > 60000) {
    return { ok: false, reason: "rendered review exceeds publication limits" };
  }
  return { ok: true };
}

function escape(value) {
  return value.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
    .replace(/([\\`*_[\]{}()#!|~])/g, "\\$1").replace(/@/g, "&#64;");
}

function render(review) {
  const findings = review.findings.map((finding) => {
    const sources = [...new Set(finding.sources.map((source) => source.split(":")[0]))].sort();
    return `**${finding.severity.toUpperCase()} [${sources.join(" + ") || "general"}]: ${escape(finding.title)}**\n\n` +
      `${escape(finding.path)}${finding.line === null ? "" : `:${finding.line}`}\n\n${escape(finding.body)}`;
  });
  return `## Advisory automated review\n\n${escape(review.summary)}\n\n${findings.join("\n\n---\n\n") || "No validated findings."}` +
    "\n\nAutomated assistance, not approval. Maintainer review remains required.";
}

function readJson(file) {
  const stat = fs.lstatSync(file);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 8 * 1024 * 1024) {
    throw new Error("Invalid trusted JSON input");
  }
  return JSON.parse(fs.readFileSync(file, "utf8"));
}

function agentValidator(value, { metadata, previousCandidate }) {
  let context, aggregate;
  try {
    context = readJson(metadata.context_file);
    aggregate = metadata.stage === "general" ? readJson("pr-evidence/aggregate.json") : [];
  } catch {
    const error = new Error("trusted validation input unavailable");
    error.code = "VALIDATOR_TERMINAL";
    throw error;
  }
  const result = validate(value, metadata.stage, context, aggregate);
  if (!result.ok) return result;
  // Repair cannot silently discard a previously proposed finding to satisfy validation.
  if (Array.isArray(previousCandidate?.findings) && previousCandidate.findings.some((entry) =>
    typeof entry?.id === "string" && !value.findings?.some((finding) => finding.id === entry.id))) {
    return { ok: false, reason: "repair must preserve previously proposed finding IDs" };
  }
  return result;
}

module.exports = { validate, escape, render, readJson, agentValidator };
