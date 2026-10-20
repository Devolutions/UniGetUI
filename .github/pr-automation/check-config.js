"use strict";

const fs = require("node:fs");
const path = require("node:path");
const YAML = require("yaml");
const { policy, REVIEWERS, workflows, modes } = require("./policy");
const profiles = require("./agents/profiles.json");
const labels = require("./labels.json");
require("./validation");
const root = path.resolve(__dirname, "..", "..");

if (policy.version !== 1 || policy.defaultMode !== "report-only" || policy.defaultAiEnabled !== false ||
    policy.maxReviews !== 3 || policy.maxParallel !== 3 || policy.maxFiles !== 3000 ||
    policy.maxDiffBytes !== 4194304 || policy.maxTreeBytes !== 67108864) throw new Error("Invalid rollout/safety policy");
if (new Set(labels.map((label) => label.name)).size !== labels.length ||
    labels.some((label) => !/^[a-z0-9/-]+$/i.test(label.name) || !/^[a-f0-9]{6}$/.test(label.color))) {
  throw new Error("Invalid label manifest");
}
for (const stage of ["classifier", ...REVIEWERS, "general"]) {
  if (!profiles[stage]) throw new Error("Missing reviewer profile");
  for (const file of [...profiles[stage].methodologies, `.github/pr-automation/prompts/${profiles[stage].prompt}.md`]) {
    if (!fs.statSync(path.join(root, file)).isFile()) throw new Error("Missing trusted methodology");
  }
}
for (const file of fs.readdirSync(path.join(root, ".github", "workflows"))) {
  const workflow = YAML.parse(fs.readFileSync(path.join(root, ".github", "workflows", file), "utf8"));
  if (!workflow.name || !workflow.on || !workflow.jobs) throw new Error("Invalid workflow document");
}
const orchestrator = YAML.parse(fs.readFileSync(path.join(root, ".github", "workflows", "pr-automation.yml"), "utf8"));
const pipeline = YAML.parse(fs.readFileSync(path.join(root, ".github", "workflows", "review-pipeline.yml"), "utf8"));
if (orchestrator.jobs.publication.concurrency.queue !== "max" ||
    orchestrator.jobs.publication.concurrency["cancel-in-progress"] !== false ||
    !orchestrator.jobs.publication.if.includes("vars.PR_AUTOMATION_MODE == 'publish'")) {
  throw new Error("Publication must remain opt-in and serialized");
}
for (const job of Object.values(pipeline.jobs)) {
  if (Object.values(job.permissions).some((value) => value !== "read") ||
      job.steps.filter((step) => step.uses?.startsWith("actions/checkout@"))
        .some((step) => step.with.ref !== "${{ github.workflow_sha }}" || step.with["persist-credentials"] !== false)) {
    throw new Error("Model pipeline must use read-only permissions and immutable trusted code");
  }
}
if (Object.keys(pipeline.on).join(",") !== "workflow_call") throw new Error("Review pipeline must only be callable");
workflows(root, "main", [{ filename: ".github/workflows/pr-automation-tests.yml" }]);
if (modes({}).mode !== "report-only" || modes({}).aiEnabled) throw new Error("Unsafe default mode");
console.log("Automation configuration, schemas, methodologies and CI filters are consistent.");
