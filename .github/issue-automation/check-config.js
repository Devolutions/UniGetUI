"use strict";

const fs = require("node:fs");
const path = require("node:path");
const YAML = require("yaml");
const Ajv = require("ajv");

const root = path.resolve(__dirname, "..", "..");
const ajv = new Ajv({ allErrors: true, strict: true });
const policy = require("./policy.json");
const labels = require("./labels.json");
const capabilities = require("./capabilities.json");
const profiles = require("./agents/profiles.json");
const { OWNED_LABELS, PROTECTED_LABELS, STAGES } = require("./policy");

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

assert(policy.defaultMode === "report-only", "Issue automation defaultMode must be report-only");
assert(policy.defaultAiEnabled === false, "Issue automation defaultAiEnabled must be false");
assert(policy.defaultDailyBudget === 0, "Issue automation defaultDailyBudget must be 0");

const seenLabels = new Set();
for (const entry of labels) {
  assert(entry.name && !seenLabels.has(entry.name), `Duplicate or missing label: ${entry.name}`);
  seenLabels.add(entry.name);
  assert(/^[0-9a-fA-F]{6}$/.test(entry.color), `Invalid color for label ${entry.name}`);
  assert(
    ["opt-in", "automation-additive", "human-protected"].includes(entry.ownership),
    `Invalid ownership for label ${entry.name}`,
  );
}

for (const owned of OWNED_LABELS) {
  assert(!PROTECTED_LABELS.has(owned), `Label '${owned}' cannot be both owned and protected`);
  const found = labels.find((l) => l.name === owned);
  assert(found && found.ownership === "automation-additive", `Owned label '${owned}' missing from labels.json`);
}

for (const prot of PROTECTED_LABELS) {
  const found = labels.find((l) => l.name === prot);
  assert(found && ["human-protected", "opt-in"].includes(found.ownership), `Protected label '${prot}' missing from labels.json`);
}

assert(Array.isArray(capabilities.capabilities) && capabilities.capabilities.length >= 4, "capabilities.json must list shipped capabilities");
for (const cap of capabilities.capabilities) {
  assert(cap.id && cap.sinceRelease && cap.shippedScope && cap.unmetOrDistinctScope, `Incomplete capability ${cap.id}`);
}

for (const stage of STAGES) {
  ajv.compile(require(`./schemas/${stage}.json`));
  const profile = profiles[stage];
  assert(profile, `Missing profile for stage ${stage}`);
  assert(fs.existsSync(path.join(root, `.github/issue-automation/prompts/${profile.prompt}.md`)), `Missing prompt for ${stage}`);
  for (const file of profile.methodologies) {
    assert(fs.existsSync(path.join(root, file)), `Missing methodology file ${file}`);
  }
}

for (const file of [
  ".github/workflows/issue-automation.yml",
  ".github/ISSUE_TEMPLATE/bug-issue.yml",
  ".github/ISSUE_TEMPLATE/hard-crash.yml",
  ".github/ISSUE_TEMPLATE/config.yml",
]) {
  YAML.parse(fs.readFileSync(path.join(root, file), "utf8"));
}
console.log("Issue automation configuration, schemas, labels, capabilities, and templates are consistent.");
