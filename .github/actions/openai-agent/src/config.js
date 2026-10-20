"use strict";

const Ajv = require("ajv");

const { fail } = require("./errors");
const {
  DEFAULT_OUTPUT_REPAIRS, DEFAULT_REQUEST_RETRIES, DEFAULT_STAGE_TIMEOUT_MS,
  DEFAULT_STREAM_IDLE_TIMEOUT_MS,
  MAX_CONFIG_BYTES, MAX_METHODOLOGY_BYTES, MAX_METHODOLOGY_TOTAL_BYTES, MAX_PROMPT_BYTES,
  MAX_PROMPT_CONTEXT_BYTES,
  MAX_OUTPUT_REPAIRS, MAX_REQUEST_RETRIES, MAX_STAGE_TIMEOUT_MS, MAX_STREAM_IDLE_TIMEOUT_MS,
  MAX_SCHEMA_BYTES, MAX_TOOL_CALLS, MAX_TURNS,
} = require("./limits");
const { WorkspaceSandbox } = require("./sandbox");

const SAFE_ID = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/;
const SAFE_MODEL = /^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$/;

const CONFIG_SCHEMA = {
  type: "object",
  additionalProperties: false,
  required: [
    "id", "model", "prompt_file", "schema_file", "allowed_roots", "allowed_files",
    "max_turns", "max_tool_calls",
  ],
  properties: {
    id: { type: "string", pattern: SAFE_ID.source },
    model: { type: "string", pattern: SAFE_MODEL.source },
    prompt_file: { type: "string", minLength: 1 },
    schema_file: { type: "string", minLength: 1 },
    methodology_files: {
      type: "array",
      maxItems: 32,
      items: { type: "string", minLength: 1 },
    },
    allowed_roots: {
      type: "array",
      maxItems: 32,
      uniqueItems: true,
      items: { type: "string", minLength: 1 },
    },
    allowed_files: {
      type: "array",
      maxItems: 256,
      uniqueItems: true,
      items: { type: "string", minLength: 1 },
    },
    max_turns: { type: "integer", minimum: 1, maximum: MAX_TURNS },
    max_tool_calls: { type: "integer", minimum: 0, maximum: MAX_TOOL_CALLS },
    stream_idle_timeout_ms: {
      type: "integer", minimum: 1, maximum: MAX_STREAM_IDLE_TIMEOUT_MS,
    },
    stage_timeout_ms: { type: "integer", minimum: 1, maximum: MAX_STAGE_TIMEOUT_MS },
    max_request_retries: { type: "integer", minimum: 0, maximum: MAX_REQUEST_RETRIES },
    max_output_repair_attempts: { type: "integer", minimum: 0, maximum: MAX_OUTPUT_REPAIRS },
    output_format: { enum: ["json_object", "json_schema"] },
  },
};

function parseJson(text, code) {
  try {
    return JSON.parse(text);
  } catch {
    fail(code);
  }
}

// The prompt context is trusted workflow text computed for this invocation, such as the identifiers a
// stage must echo back, so the model has them before it spends any turn looking for them.
function loadConfiguration(workspace, configFile, promptContext = "") {
  if (typeof promptContext !== "string" ||
      Buffer.byteLength(promptContext, "utf8") > MAX_PROMPT_CONTEXT_BYTES) {
    fail("prompt context exceeds byte limit");
  }
  const workspaceReader = new WorkspaceSandbox(workspace);
  const rawConfig = workspaceReader.readWorkflowFile(configFile, MAX_CONFIG_BYTES);
  const parsed = parseJson(rawConfig, "configuration is not valid JSON");
  const validate = new Ajv({ allErrors: true, strict: true }).compile(CONFIG_SCHEMA);
  if (!validate(parsed)) fail("configuration does not match its schema");
  const config = {
    ...parsed,
    stream_idle_timeout_ms: parsed.stream_idle_timeout_ms ?? DEFAULT_STREAM_IDLE_TIMEOUT_MS,
    stage_timeout_ms: parsed.stage_timeout_ms ?? DEFAULT_STAGE_TIMEOUT_MS,
    max_request_retries: parsed.max_request_retries ?? DEFAULT_REQUEST_RETRIES,
    max_output_repair_attempts: parsed.max_output_repair_attempts ?? DEFAULT_OUTPUT_REPAIRS,
    output_format: parsed.output_format || "json_object",
  };
  if (config.max_tool_calls > 0 &&
      config.allowed_roots.length === 0 && config.allowed_files.length === 0) {
    fail("configuration grants no filesystem capabilities");
  }
  const sandbox = new WorkspaceSandbox(workspace, {
    allowedRoots: config.allowed_roots,
    allowedFiles: config.allowed_files,
  });
  const configuredPrompt = workspaceReader.readWorkflowFile(config.prompt_file, MAX_PROMPT_BYTES);
  const prompt = promptContext === ""
    ? configuredPrompt
    : `${configuredPrompt.trimEnd()}\n\n${promptContext}`;
  const schemaText = workspaceReader.readWorkflowFile(config.schema_file, MAX_SCHEMA_BYTES);
  const schema = parseJson(schemaText, "output schema is not valid JSON");
  if (schema === null || typeof schema !== "object" || Array.isArray(schema)) {
    fail("output schema must be an object");
  }
  if (config.output_format === "json_schema" && schema.type !== "object") {
    fail("strict JSON Schema output requires an object schema");
  }

  const methodologies = [];
  let methodologyBytes = 0;
  for (const file of config.methodology_files || []) {
    const content = workspaceReader.readWorkflowFile(file, MAX_METHODOLOGY_BYTES);
    methodologyBytes += Buffer.byteLength(content, "utf8");
    if (methodologyBytes > MAX_METHODOLOGY_TOTAL_BYTES) fail("methodology files exceed byte limit");
    methodologies.push(content);
  }

  return { config, methodologies, prompt, sandbox, schema };
}

function validateBaseUrl(raw) {
  if (typeof raw !== "string" || raw.length === 0 || raw.length > 2048) fail("invalid base URL");
  let url;
  try {
    url = new URL(raw);
  } catch {
    fail("invalid base URL");
  }
  const localHttp = url.protocol === "http:" &&
    ["localhost", "127.0.0.1", "[::1]"].includes(url.hostname);
  if (url.protocol !== "https:" && !localHttp) fail("base URL must use HTTPS");
  if (url.username || url.password || url.search || url.hash) {
    fail("base URL must not contain credentials, a query, or a fragment");
  }
  return url.toString().replace(/\/$/, "");
}

module.exports = { CONFIG_SCHEMA, loadConfiguration, validateBaseUrl };
