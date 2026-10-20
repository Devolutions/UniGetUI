"use strict";

const OpenAI = require("openai");

const { AgentFailure, providerFailureDiagnostic, runAgent } = require("./agent");
const { loadConfiguration, validateBaseUrl } = require("./config");
const { ActionError } = require("./errors");
const { RuntimeMetrics, createProviderClient } = require("./provider");
const { validateOutputFilePath, writeOutputFile } = require("./sandbox");
const { ValidatorFailure, loadNormalizer, loadValidator, parseMetadata } = require("./validator");

async function main(core, environment = process.env, OpenAIClient = OpenAI) {
  let apiKey = "";
  let turnCount = 0;
  let toolCallCount = 0;
  let outputRepairCount = 0;
  let phase = "input";
  const metrics = new RuntimeMetrics();
  setOutputs(core, {
    output: "", failureReason: "", failureCategory: "", retryable: false,
    turnCount, toolCallCount, outputRepairCount, metrics,
  });

  try {
    apiKey = requiredInput(core, "api-key", "api key input is missing", false);
    core.setSecret(apiKey);

    const baseUrlInput = requiredInput(core, "base-url", "base URL input is missing");
    const configFile = requiredInput(core, "config-file", "config file input is missing");
    const promptContext = core.getInput("prompt-context");
    const validatorSelector = core.getInput("validator");
    const normalizerSelector = core.getInput("normalizer");
    const validatorMetadata = parseMetadata(core.getInput("validator-metadata"));
    const outputFile = validateOutputFilePath(core.getInput("structured-output-file"));
    if (validatorSelector === "" && Object.keys(validatorMetadata).length !== 0) {
      throw new ActionError("validator metadata requires a validator", "input");
    }
    phase = "configuration";
    const baseURL = validateBaseUrl(baseUrlInput);
    const workspace = environment.GITHUB_WORKSPACE;
    if (typeof workspace !== "string" || workspace.length === 0) {
      throw new ActionError("workspace is unavailable");
    }
    const loaded = loadConfiguration(workspace, configFile, promptContext);
    const config = loaded.config;
    const validator = loadValidator(workspace, validatorSelector, validatorMetadata);
    const normalizer = loadNormalizer(workspace, normalizerSelector);
    core.info(JSON.stringify({
      event: "openai-agent.start",
      id: config.id,
      model: config.model,
      stageTimeoutMs: config.stage_timeout_ms,
      streamIdleTimeoutMs: config.stream_idle_timeout_ms,
      maxTurns: config.max_turns,
      maxToolCalls: config.max_tool_calls,
    }));

    phase = "initialization";
    let client;
    try {
      client = createProviderClient(OpenAIClient, {
        apiKey,
        baseURL,
        maxRetries: 0,
        fetchOptions: { redirect: "error" },
      }, metrics);
    } catch {
      throw new ActionError("provider client initialization failed", "initialization");
    }
    phase = "runtime";
    const result = await runAgent({ client, ...loaded, config, normalizer, validator, metrics });
    turnCount = result.turnCount;
    toolCallCount = result.toolCallCount;
    outputRepairCount = result.outputRepairCount;
    writeOutputFile(workspace, outputFile, result.output);
    setOutputs(core, {
      output: outputFile === "" ? result.output : "",
      failureReason: "",
      failureCategory: "",
      retryable: false,
      turnCount, toolCallCount, outputRepairCount, metrics,
    });
    const {
      ignoredPostFinishEmptyDeltaChoices,
      ignoredRepeatedTerminalChoices,
      requestRetryCount,
      streamStructuralViolation,
      postFinishShape,
    } = metrics.snapshot();
    core.info(JSON.stringify({
      event: "openai-agent.complete",
      id: config.id,
      model: config.model,
      turnCount,
      toolCallCount,
      outputRepairCount,
      outputBytes: Buffer.byteLength(result.output, "utf8"),
      requestRetryCount,
      ignoredPostFinishEmptyDeltaChoices,
      ignoredRepeatedTerminalChoices,
      ...(streamStructuralViolation === undefined ? {} : { streamStructuralViolation }),
      ...(postFinishShape === undefined ? {} : { postFinishShape }),
    }));
  } catch (error) {
    const outcome = failure(error, phase);
    const failureReason = outcome.reason;
    // Report the violation that caused this failure, not one a request retry already recovered.
    const { streamStructuralViolation, postFinishShape } = error instanceof AgentFailure
      ? error
      : {};
    if (error instanceof AgentFailure) {
      turnCount = error.turnCount;
      toolCallCount = error.toolCallCount;
      outputRepairCount = error.outputRepairCount;
      const diagnostic = providerFailureDiagnostic(error.cause);
      metrics.recordProviderFailure(diagnostic);
      if (error.cause) {
        core.info(JSON.stringify({
          event: "openai-agent.provider-failure",
          reason: failureReason,
          category: outcome.category,
          retryable: outcome.retryable,
          ...(diagnostic || {}),
          ...(streamStructuralViolation === undefined ? {} : { streamStructuralViolation }),
          ...(postFinishShape === undefined ? {} : { postFinishShape }),
        }));
      } else {
        logActionFailure(core, "runtime", outcome, streamStructuralViolation, postFinishShape);
      }
    } else {
      logActionFailure(
        core,
        error instanceof ActionError ? error.phase : phase,
        outcome,
        streamStructuralViolation,
        postFinishShape,
      );
    }
    setOutputs(core, {
      output: "", failureReason, failureCategory: outcome.category,
      retryable: outcome.retryable,
      turnCount, toolCallCount, outputRepairCount, metrics,
    });
    core.setFailed(failureReason);
  } finally {
    apiKey = "";
  }
}

function requiredInput(core, name, failureReason, trimWhitespace = true) {
  const value = core.getInput(name, { trimWhitespace });
  if (value.length === 0) throw new ActionError(failureReason, "input");
  return value;
}

function failure(error, phase) {
  if (error instanceof AgentFailure) {
    return { reason: error.reason, category: error.category, retryable: error.retryable };
  }
  if (error instanceof ValidatorFailure) {
    return { reason: error.reason, category: error.category, retryable: false };
  }
  if (error instanceof ActionError) {
    return {
      reason: error.code,
      category: error.phase === "input" ? "input" : "configuration",
      retryable: false,
    };
  }
  switch (phase) {
    case "input": return { reason: "action input failed", category: "input", retryable: false };
    case "configuration": return { reason: "action configuration failed", category: "configuration", retryable: false };
    case "initialization": return { reason: "provider client initialization failed", category: "initialization", retryable: false };
    default: return { reason: "action runtime failed", category: "runtime", retryable: false };
  }
}

function logActionFailure(core, phase, outcome, streamStructuralViolation, postFinishShape) {
  core.info(JSON.stringify({
    event: "openai-agent.failure",
    phase,
    reason: outcome.reason,
    category: outcome.category,
    retryable: outcome.retryable,
    ...(streamStructuralViolation === undefined ? {} : { streamStructuralViolation }),
    ...(postFinishShape === undefined ? {} : { postFinishShape }),
  }));
}

function setOutputs(core, {
  output, failureReason, failureCategory, retryable, turnCount, toolCallCount, outputRepairCount,
  metrics,
}) {
  const diagnostics = metrics.snapshot({
    outputRepairCount,
    turnCount,
    toolCallCount,
    failureCategory: failureCategory || null,
    retryable,
  });
  core.setOutput("structured-output", output);
  core.setOutput("failure-reason", failureReason);
  core.setOutput("turn-count", String(turnCount));
  core.setOutput("tool-call-count", String(toolCallCount));
  core.setOutput("failure-category", failureCategory);
  core.setOutput("retryable", String(retryable));
  core.setOutput("diagnostics", JSON.stringify(diagnostics));
}

module.exports = { main, setOutputs };
