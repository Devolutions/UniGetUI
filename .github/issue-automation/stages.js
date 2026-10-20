"use strict";

const fs = require("node:fs");
const { modes, STAGES } = require("./policy");
const { readJson, validate } = require("./validation");
const profiles = require("./agents/profiles.json");

function configuration(stage, env = process.env) {
  modes({
    ...env,
    ISSUE_AUTOMATION_AI_ENABLED: "true",
    ISSUE_AUTOMATION_DAILY_BUDGET: env.ISSUE_AUTOMATION_DAILY_BUDGET || "1",
  });
  const profile = profiles[stage];
  if (!profile || !STAGES.includes(stage)) throw new Error("Unknown issue model stage");
  return {
    id: stage,
    model: env.ISSUE_AUTOMATION_MODEL,
    prompt_file: "issue-evidence/prompt.md",
    schema_file: `.github/issue-automation/schemas/${stage}.json`,
    methodology_files: profile.methodologies,
    allowed_roots: ["docs"],
    allowed_files: [
      "issue-evidence/context.json",
      ...(stage === "verifier" ? ["issue-evidence/triage.json"] : []),
    ],
    max_turns: 10,
    max_tool_calls: 24,
    stream_idle_timeout_ms: 120000,
    stage_timeout_ms: 600000,
    max_request_retries: 2,
    max_output_repair_attempts: 2,
  };
}

function prepareStage(stage, env = process.env) {
  const config = configuration(stage, env);
  const common = fs.readFileSync(".github/issue-automation/prompts/common.md", "utf8");
  const prompt = fs.readFileSync(`.github/issue-automation/prompts/${profiles[stage].prompt}.md`, "utf8");
  fs.mkdirSync("issue-evidence", { recursive: true });
  fs.writeFileSync("issue-evidence/prompt.md", `${common}\n\n${prompt}\n\nExpected stage: ${stage}.\n`);
  fs.writeFileSync("issue-evidence/agent-config.json", JSON.stringify(config));
  return config;
}

function recordTriage() {
  const context = readJson("issue-evidence/context.json");
  const triage = readJson(".openai-agent-output/triage.json");
  const valid = validate(triage, "triage", context);
  if (!valid.ok) throw new Error(valid.reason);
  fs.writeFileSync("issue-evidence/triage.json", JSON.stringify(triage));
  const needsVerifier = !triage.abstain && (
    triage.ownershipHypothesis.category !== "unknown" ||
    triage.capabilityMatches.length > 0 ||
    triage.proposedLabels.some((l) => !context.deterministic.proposedLabels.includes(l))
  );
  return { triage, needsVerifier };
}

function finishIssueStage(runId) {
  const context = readJson("issue-evidence/context.json");
  const triage = readJson("issue-evidence/triage.json");
  if (!validate(triage, "triage", context).ok) throw new Error("Triage validation failed");
  let verifier = null;
  let finalLabels = triage.proposedLabels;
  if (fs.existsSync(".openai-agent-output/verifier.json")) {
    verifier = readJson(".openai-agent-output/verifier.json");
    const valid = validate(verifier, "verifier", context, triage);
    if (!valid.ok) throw new Error(valid.reason);
    finalLabels = verifier.approvedLabels;
  }
  const labelsToAdd = finalLabels.filter((l) => !context.existingLabels.includes(l));
  const report = {
    version: 1,
    runId: String(runId),
    number: context.number,
    fingerprint: context.fingerprint,
    automationSha: context.automationSha,
    context,
    triage,
    verifier,
    finalLabels,
    labelsToAdd,
  };
  fs.mkdirSync("issue-automation-results", { recursive: true });
  fs.writeFileSync(`issue-automation-results/result-${context.number}.json`, JSON.stringify(report));
  return report;
}

if (require.main === module) {
  const [command, argument] = process.argv.slice(2);
  if (command === "prepare") {
    prepareStage(argument);
  } else if (command === "record-triage") {
    const { needsVerifier } = recordTriage();
    if (process.env.GITHUB_OUTPUT) {
      fs.appendFileSync(process.env.GITHUB_OUTPUT, `needs-verifier=${needsVerifier}\n`);
    }
  } else if (command === "finish") {
    finishIssueStage(process.env.GITHUB_RUN_ID);
  } else {
    throw new Error("Unknown issue stage command");
  }
}

module.exports = {
  configuration,
  prepareStage,
  recordTriage,
  finishIssueStage,
};
