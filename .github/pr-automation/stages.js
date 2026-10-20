"use strict";

const fs = require("node:fs");
const path = require("node:path");
const { route, modes } = require("./policy");
const { readJson, validate } = require("./validation");
const profiles = require("./agents/profiles.json");

function configuration(stage, env = process.env) {
  modes({ ...env, AUTOMATION_AI_ENABLED: "true" });
  const profile = profiles[stage];
  if (!profile) throw new Error("Unknown model stage");
  return {
    id: stage, model: env.AUTOMATION_MODEL,
    prompt_file: "pr-evidence/prompt.md",
    schema_file: `.github/pr-automation/schemas/${stage === "classifier" ? "classifier" : "review"}.json`,
    methodology_files: profile.methodologies,
    allowed_roots: ["pr-head"],
    allowed_files: ["pr-evidence/context.json", "pr-evidence/pull-request.diff",
      ...(stage === "general" ? ["pr-evidence/aggregate.json"] : [])],
    max_turns: stage === "general" ? 24 : 16, max_tool_calls: stage === "general" ? 100 : 64,
    stream_idle_timeout_ms: 120000, stage_timeout_ms: 1200000,
    max_request_retries: 2, max_output_repair_attempts: 2,
  };
}

function prepareStage(stage) {
  const config = configuration(stage);
  const common = fs.readFileSync(".github/pr-automation/prompts/common.md", "utf8");
  const prompt = fs.readFileSync(`.github/pr-automation/prompts/${profiles[stage].prompt}.md`, "utf8");
  fs.writeFileSync("pr-evidence/prompt.md", `${common}\n\n${prompt}\n\nExpected reviewer: ${stage}.\n`);
  fs.writeFileSync("pr-evidence/agent-config.json", JSON.stringify(config));
}

function selectReviewers() {
  const context = readJson("pr-evidence/context.json");
  const classifier = readJson(".openai-agent-output/classifier.json");
  const valid = validate(classifier, "classifier", context);
  if (!valid.ok) throw new Error(valid.reason);
  const snapshot = readJson("pr-evidence/snapshot.json");
  const selected = route(snapshot.analysis, classifier);
  fs.writeFileSync("pr-evidence/classifier.json", JSON.stringify(classifier));
  fs.writeFileSync("pr-evidence/selected.json", JSON.stringify(selected));
  return selected;
}

function aggregate(directory) {
  const context = readJson("pr-evidence/context.json");
  const selected = readJson("pr-evidence/selected.json");
  const outputs = selected.map((stage) => {
    const value = readJson(path.join(directory, `${stage}.json`));
    const result = validate(value, stage, context);
    if (!result.ok) throw new Error(result.reason);
    return value;
  });
  if (Buffer.byteLength(JSON.stringify(outputs)) > 1024 * 1024) throw new Error("Aggregate exceeds limits");
  fs.writeFileSync("pr-evidence/aggregate.json", JSON.stringify(outputs));
}

function finish(runId) {
  const context = readJson("pr-evidence/context.json");
  const classifier = readJson("pr-evidence/classifier.json");
  const outputs = readJson("pr-evidence/aggregate.json");
  const final = readJson(".openai-agent-output/general.json");
  const result = validate(final, "general", context, outputs);
  if (!result.ok || !validate(classifier, "classifier", context).ok) throw new Error("Final validation failed");
  const report = {
    version: 1, runId, number: context.number, head: context.head, base: context.base,
    automationSha: context.automationSha, context, classifier, specialists: outputs, review: final,
  };
  fs.mkdirSync("automation-results", { recursive: true });
  fs.writeFileSync(`automation-results/result-${context.number}.json`, JSON.stringify(report));
}

if (require.main === module) {
  const [command, argument] = process.argv.slice(2);
  if (command === "prepare") prepareStage(argument);
  else if (command === "select") {
    const selected = selectReviewers();
    fs.appendFileSync(process.env.GITHUB_OUTPUT, `reviewers=${JSON.stringify(selected)}\n`);
  } else if (command === "aggregate") aggregate(argument);
  else if (command === "finish") finish(process.env.GITHUB_RUN_ID);
  else throw new Error("Unknown stage command");
}

module.exports = { configuration, prepareStage, selectReviewers, aggregate, finish };
