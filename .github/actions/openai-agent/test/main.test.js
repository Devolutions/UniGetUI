"use strict";

const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const assert = require("node:assert/strict");
const { APIConnectionError, APIConnectionTimeoutError } = require("openai");

const { main } = require("../src/main");
const { scratchWorkspace, write } = require("./helpers");

test("action metadata exposes only configured inputs and required outputs on node24", () => {
  const action = fs.readFileSync(path.join(__dirname, "..", "action.yml"), "utf8");
  assert.match(action, /runs:\r?\n  using: node24\r?\n  main: dist\/index\.js/);
  for (const input of [
    "api-key", "base-url", "config-file", "prompt-context", "validator", "normalizer",
    "validator-metadata", "structured-output-file",
  ]) {
    assert.match(action, new RegExp(`^  ${input}:\\r?$`, "m"));
  }
  for (const output of [
    "structured-output", "failure-reason", "turn-count", "tool-call-count", "diagnostics",
    "failure-category", "retryable",
  ]) {
    assert.match(action, new RegExp(`^  ${output}:\\r?$`, "m"));
  }
  assert.match(action, /Number of model turns attempted, excluding SDK retries/);
  assert.equal(fs.existsSync(path.join(__dirname, "..", "dist", "index.js")), true);
  assert.equal(fs.existsSync(path.join(__dirname, "..", "dist", "licenses.txt")), true);
});

function actionFixture() {
  const workspace = scratchWorkspace();
  fs.mkdirSync(`${workspace.directory}/evidence`);
  write(workspace.directory, "prompt.md", "PROMPT_SECRET_SENTINEL");
  write(workspace.directory, "schema.json", JSON.stringify({
    type: "object",
    additionalProperties: false,
    required: ["answer"],
    properties: { answer: { type: "string" } },
  }));
  write(workspace.directory, "config.json", JSON.stringify({
    id: "safe-id",
    model: "safe-model",
    prompt_file: "prompt.md",
    schema_file: "schema.json",
    methodology_files: [],
    allowed_roots: ["evidence"],
    allowed_files: [],
    max_turns: 3,
    max_tool_calls: 2,
    max_request_retries: 0,
  }));
  return workspace;
}

function mockCore(inputs) {
  const events = [];
  const outputs = new Map();
  return {
    events,
    outputs,
    getInput(name) {
      events.push(["input", name]);
      return inputs[name] || "";
    },
    setSecret(value) {
      events.push(["secret", value]);
    },
    setOutput(name, value) {
      events.push(["output", name, value]);
      outputs.set(name, value);
    },
    info(value) {
      events.push(["info", value]);
    },
    setFailed(value) {
      events.push(["failed", value]);
    },
  };
}

function completionStream(response) {
  return {
    controller: new AbortController(),
    async *[Symbol.asyncIterator]() {
      const source = response.choices[0].message;
      const toolCalls = Array.isArray(source.tool_calls)
        ? source.tool_calls.map((toolCall, index) => ({ index, ...toolCall }))
        : undefined;
      yield {
        choices: [{
          index: 0,
          delta: {
            ...(source.content === undefined ? {} : { content: source.content }),
            ...(toolCalls === undefined ? {} : { tool_calls: toolCalls }),
          },
          finish_reason: toolCalls?.length ? "tool_calls" : "stop",
        }],
      };
    },
  };
}

test("main masks the key immediately, rejects redirects, and emits only bounded metadata", async () => {
  const workspace = actionFixture();
  const core = mockCore({
    "api-key": "API_KEY_SECRET_SENTINEL",
    "base-url": "https://provider.example/v1",
    "config-file": "config.json",
  });
  let options;
  let request;
  class MockOpenAI {
    constructor(received) {
      options = received;
      this.chat = { completions: { create: async (value) => {
        request = value;
        return {
          controller: new AbortController(),
          async *[Symbol.asyncIterator]() {
            yield {
              choices: [{
                index: 0,
                delta: { content: '{"answer":"MODEL_RESPONSE_SENTINEL"}' },
                finish_reason: "stop",
              }],
            };
            yield { choices: [{ index: 0, delta: {} }] };
          },
        };
      } } };
    }
  }
  try {
    await main(core, { GITHUB_WORKSPACE: workspace.directory }, MockOpenAI);
    const inputIndex = core.events.findIndex((event) => event[0] === "input" && event[1] === "api-key");
    const secretIndex = core.events.findIndex((event) => event[0] === "secret");
    const secondInputIndex = core.events.findIndex(
      (event, index) => index > inputIndex && event[0] === "input",
    );
    assert.equal(secretIndex, inputIndex + 1);
    assert.equal(secretIndex < secondInputIndex, true);
    assert.equal(options.apiKey, "API_KEY_SECRET_SENTINEL");
    assert.equal(options.baseURL, "https://provider.example/v1");
    assert.equal(options.maxRetries, 0);
    assert.equal(options.timeout, undefined);
    assert.deepEqual(options.fetchOptions, { redirect: "error" });
    assert.equal(request.model, "safe-model");
    assert.equal(request.stream, true);
    assert.equal(request.reasoning_effort, "high");
    assert.deepEqual(request.stream_options, { include_usage: true });
    assert.equal(request.max_tokens, undefined);
    assert.equal(core.outputs.get("structured-output"), '{"answer":"MODEL_RESPONSE_SENTINEL"}');
    assert.equal(core.outputs.get("failure-reason"), "");
    assert.equal(core.outputs.get("turn-count"), "1");
    assert.equal(core.outputs.get("tool-call-count"), "0");
    assert.equal(core.events.some((event) => event[0] === "failed"), false);

    const logs = core.events.filter((event) => event[0] === "info").map((event) => event[1]).join("\n");
    for (const forbidden of [
      "API_KEY_SECRET_SENTINEL", "PROMPT_SECRET_SENTINEL", "MODEL_RESPONSE_SENTINEL",
      "provider.example",
    ]) {
      assert.doesNotMatch(logs, new RegExp(forbidden));
    }
    const completion = core.events.filter((event) => event[0] === "info").map((event) => JSON.parse(event[1]))
      .find((event) => event.event === "openai-agent.complete");
    assert.equal(completion.ignoredPostFinishEmptyDeltaChoices, 1);
    assert.equal(completion.ignoredRepeatedTerminalChoices, 0);
  } finally {
    workspace.cleanup();
  }
});

test("main appends the trusted prompt context to the configured prompt", async () => {
  const workspace = actionFixture();
  const core = mockCore({
    "api-key": "key",
    "base-url": "https://provider.example/v1",
    "config-file": "config.json",
    "prompt-context": "PROMPT_CONTEXT_SENTINEL",
  });
  const requests = [];
  class ContextOpenAI {
    constructor() {
      this.chat = { completions: { create: async (request) => {
        requests.push(request);
        return completionStream({ choices: [{ message: { content: '{"answer":"done"}' } }] });
      } } };
    }
  }
  try {
    await main(core, { GITHUB_WORKSPACE: workspace.directory }, ContextOpenAI);
    assert.equal(core.outputs.get("failure-reason"), "");
    const prompt = requests[0].messages.find((message) => message.role === "user").content;
    assert.equal(prompt, "PROMPT_SECRET_SENTINEL\n\nPROMPT_CONTEXT_SENTINEL");
    const emitted = JSON.stringify(core.events.filter(([kind]) => ["output", "info", "failed"].includes(kind)));
    assert.ok(!emitted.includes("PROMPT_CONTEXT_SENTINEL"), emitted);
  } finally {
    workspace.cleanup();
  }
});

test("main transports accepted output through a workflow-controlled file without an output value", async () => {
  const workspace = actionFixture();
  const answer = "😀".repeat(36_000);
  assert.ok(Buffer.byteLength(JSON.stringify({ answer }), "utf8") > 128 * 1024);
  const core = mockCore({
    "api-key": "key",
    "base-url": "https://provider.example/v1",
    "config-file": "config.json",
    "structured-output-file": ".openai-agent-output/review.json",
  });
  class MockOpenAI {
    constructor() {
      this.chat = { completions: { create: async () => completionStream({
        choices: [{ message: { content: JSON.stringify({ answer }) } }],
      }) } };
    }
  }
  try {
    await main(core, { GITHUB_WORKSPACE: workspace.directory }, MockOpenAI);
    assert.equal(core.outputs.get("structured-output"), "");
    assert.equal(core.outputs.has("structured-output-file"), false);
    assert.equal(
      fs.readFileSync(path.join(workspace.directory, ".openai-agent-output", "review.json"), "utf8"),
      JSON.stringify({ answer }),
    );
  } finally {
    workspace.cleanup();
  }
});

test("configuration supplies recovery limits and canonical diagnostics", async () => {
  const workspace = actionFixture();
  write(workspace.directory, "config.json", JSON.stringify({
    id: "safe-id",
    model: "safe-model",
    prompt_file: "prompt.md",
    schema_file: "schema.json",
    methodology_files: [],
    allowed_roots: ["evidence"],
    allowed_files: [],
    max_turns: 4,
    max_tool_calls: 1,
    stream_idle_timeout_ms: 90_000,
    stage_timeout_ms: 900_000,
    max_request_retries: 4,
    max_output_repair_attempts: 2,
  }));
  const core = mockCore({
    "api-key": "key",
    "base-url": "https://provider.example/v1",
    "config-file": "config.json",
  });
  let options;
  class MockOpenAI {
    constructor(received) {
      options = received;
      this.chat = { completions: { create: async () => completionStream({
        choices: [{ message: { content: '{"answer":"ok"}' } }],
      }) } };
    }
  }
  try {
    await main(core, { GITHUB_WORKSPACE: workspace.directory }, MockOpenAI);
    assert.equal(options.timeout, undefined);
    assert.equal(options.maxRetries, 0);
    assert.equal(core.outputs.get("turn-count"), "1");
    assert.equal(core.outputs.get("failure-category"), "");
    assert.equal(core.outputs.get("retryable"), "false");
    const diagnostics = JSON.parse(core.outputs.get("diagnostics"));
    assert.equal(Number.isSafeInteger(diagnostics.durationMs), true);
    assert.equal(diagnostics.durationMs >= 0, true);
    assert.deepEqual({ ...diagnostics, durationMs: 0 }, {
      activity: "investigating",
      durationMs: 0,
      requestRetryCount: 0,
      outputRepairCount: 0,
      providerFinishReason: null,
      tokenUsage: { complete: false, knownAttemptCount: 0, unknownAttemptCount: 0 },
      turnCount: 1,
      toolCallCount: 0,
      failureCategory: null,
      retryable: false,
      providerAttempts: [],
      ignoredPostFinishEmptyDeltaChoices: 0,
      ignoredRepeatedTerminalChoices: 0,
    });
  } finally {
    workspace.cleanup();
  }
});

test("main repairs a final response with no text", async () => {
  const workspace = actionFixture();
  const core = mockCore({
    "api-key": "key",
    "base-url": "https://provider.example/v1",
    "config-file": "config.json",
  });
  const responses = [
    { choices: [{ message: { content: null } }] },
    { choices: [{ message: { content: '{"answer":"repaired"}' } }] },
  ];
  const requests = [];
  class EmptyResponseOpenAI {
    constructor() {
      this.chat = { completions: { create: async (request) => {
        requests.push(structuredClone(request));
        return completionStream(responses.shift());
      } } };
    }
  }
  try {
    await main(core, { GITHUB_WORKSPACE: workspace.directory }, EmptyResponseOpenAI);
    assert.equal(core.outputs.get("structured-output"), '{"answer":"repaired"}');
    assert.equal(core.outputs.get("failure-reason"), "");
    assert.equal(core.outputs.get("turn-count"), "2");
    assert.equal(requests[1].tools, undefined);
    assert.deepEqual(requests[1].response_format, { type: "json_object" });
    assert.match(requests[1].messages.at(-1).content, /response was empty/);
    assert.equal(core.events.some((event) => event[0] === "failed"), false);
  } finally {
    workspace.cleanup();
  }
});

test("main never logs or outputs raw provider errors", async () => {
  const workspace = actionFixture();
  const core = mockCore({
    "api-key": "API_KEY_SECRET_SENTINEL",
    "base-url": "https://provider.example/v1",
    "config-file": "config.json",
  });
  class FailingOpenAI {
    constructor() {
      this.chat = { completions: { create: async () => {
        throw Object.assign(new Error("RAW_PROVIDER_SECRET_SENTINEL"), {
          status: 401,
          error: { message: "RAW_PROVIDER_SECRET_SENTINEL" },
        });
      } } };
    }
  }
  try {
    await main(core, { GITHUB_WORKSPACE: workspace.directory }, FailingOpenAI);
    assert.equal(core.outputs.get("structured-output"), "");
    assert.equal(core.outputs.get("failure-reason"), "provider credential rejected");
    assert.equal(core.outputs.get("turn-count"), "1");
    assert.deepEqual(
      core.events.filter((event) => event[0] === "failed").map((event) => event[1]),
      ["provider credential rejected"],
    );
    const observable = JSON.stringify(core.events);
    assert.doesNotMatch(observable, /RAW_PROVIDER_SECRET_SENTINEL/);
    assert.deepEqual(
      core.events.filter((event) => event[0] === "info").map((event) => JSON.parse(event[1]))
        .find((event) => event.event === "openai-agent.provider-failure"),
      {
        event: "openai-agent.provider-failure",
        reason: "provider credential rejected",
        category: "provider-credential",
        retryable: false,
        status: 401,
      },
    );
  } finally {
    workspace.cleanup();
  }
});

test("main emits a bounded provider request ID without raw errors", async () => {
  const workspace = actionFixture();
  const core = mockCore({
    "api-key": "API_KEY_SECRET_SENTINEL",
    "base-url": "https://provider.example/v1",
    "config-file": "config.json",
  });
  class FailingOpenAI {
    constructor() {
      this.chat = { completions: { create: async () => {
        throw Object.assign(new Error("RAW_PROVIDER_SECRET_SENTINEL"), {
          status: 403,
          requestID: "req_safe-123",
          code: "access_denied",
          headers: { get: () => "RAW_HEADER_SECRET_SENTINEL" },
        });
      } } };
    }
  }
  try {
    await main(core, { GITHUB_WORKSPACE: workspace.directory }, FailingOpenAI);
    assert.equal(core.outputs.get("failure-reason"), "provider access forbidden");
    const observable = JSON.stringify(core.events);
    assert.deepEqual(
      core.events.filter((event) => event[0] === "info").map((event) => JSON.parse(event[1]))
        .find((event) => event.event === "openai-agent.provider-failure"),
      {
        event: "openai-agent.provider-failure",
        reason: "provider access forbidden",
        category: "provider-access",
        retryable: false,
        status: 403,
        requestId: "req_safe-123",
        providerCode: "access_denied",
      },
    );
    assert.equal(JSON.parse(core.outputs.get("diagnostics")).providerErrorCode, "access_denied");
    assert.doesNotMatch(observable, /RAW_PROVIDER_SECRET_SENTINEL|RAW_HEADER_SECRET_SENTINEL/);
  } finally {
    workspace.cleanup();
  }
});

test("main distinguishes provider quota and service failures", async () => {
  const workspace = actionFixture();
  const inputs = {
    "api-key": "API_KEY_SECRET_SENTINEL",
    "base-url": "https://provider.example/v1",
    "config-file": "config.json",
  };
  try {
    for (const [status, reason, category, retryable] of [
      [429, "provider rate limit reached", "provider-rate-limit", true],
      [503, "provider service unavailable", "provider-service", true],
    ]) {
      const core = mockCore(inputs);
      class FailingOpenAI {
        constructor() {
          this.chat = { completions: { create: async () => {
            throw Object.assign(new Error("RAW_PROVIDER_SECRET_SENTINEL"), { status });
          } } };
        }
      }
      await main(core, { GITHUB_WORKSPACE: workspace.directory }, FailingOpenAI);
      assert.equal(core.outputs.get("failure-reason"), reason);
      assert.deepEqual(
        core.events.filter((event) => event[0] === "info").map((event) => JSON.parse(event[1]))
          .find((event) => event.event === "openai-agent.provider-failure"),
        { event: "openai-agent.provider-failure", reason, category, retryable, status },
      );
      assert.doesNotMatch(JSON.stringify(
        core.events.filter((event) => event[0] !== "secret"),
      ), /RAW_PROVIDER_SECRET_SENTINEL|API_KEY_SECRET_SENTINEL/);
    }
  } finally {
    workspace.cleanup();
  }
});

test("main safely distinguishes provider transport failures", async () => {
  const workspace = actionFixture();
  const inputs = {
    "api-key": "API_KEY_SECRET_SENTINEL",
    "base-url": "https://provider.example/v1",
    "config-file": "config.json",
  };
  try {
    for (const [error, reason, category] of [
      [
        new APIConnectionTimeoutError({ message: "RAW_TIMEOUT_SECRET_SENTINEL" }),
        "provider request timed out", "provider-timeout",
      ],
      [
        new APIConnectionError({
          message: "RAW_CONNECTION_SECRET_SENTINEL",
          cause: new Error("RAW_CAUSE_SECRET_SENTINEL"),
        }),
        "provider connection failed", "provider-connection",
      ],
    ]) {
      const core = mockCore(inputs);
      class FailingOpenAI {
        constructor() {
          this.chat = { completions: { create: async () => { throw error; } } };
        }
      }
      await main(core, { GITHUB_WORKSPACE: workspace.directory }, FailingOpenAI);
      assert.equal(core.outputs.get("failure-reason"), reason);
      assert.deepEqual(
        core.events.filter((event) => event[0] === "info").map((event) => JSON.parse(event[1]))
          .find((event) => event.event === "openai-agent.provider-failure"),
        { event: "openai-agent.provider-failure", reason, category, retryable: true },
      );
      assert.doesNotMatch(
        JSON.stringify(core.events.filter((event) => event[0] !== "secret")),
        /RAW_TIMEOUT_SECRET_SENTINEL|RAW_CONNECTION_SECRET_SENTINEL|RAW_CAUSE_SECRET_SENTINEL|API_KEY_SECRET_SENTINEL/,
      );
    }
  } finally {
    workspace.cleanup();
  }
});

test("main logs only static post-finish diagnostics", async () => {
  const workspace = actionFixture();
  const core = mockCore({
    "api-key": "key",
    "base-url": "https://provider.example/v1",
    "config-file": "config.json",
  });
  const sentinel = "MODEL_STREAM_SECRET_SENTINEL";
  class MalformedStreamOpenAI {
    constructor() {
      this.chat = { completions: { create: async () => ({
        controller: new AbortController(),
        async *[Symbol.asyncIterator]() {
          yield {
            choices: [{
              index: 0,
              delta: { content: '{"answer":"done"}' },
              finish_reason: "stop",
            }],
          };
          yield {
            choices: [{
              index: 0,
              delta: { content: sentinel },
            }],
          };
        },
      }) } };
    }
  }
  try {
    await main(core, { GITHUB_WORKSPACE: workspace.directory }, MalformedStreamOpenAI);
    assert.equal(core.outputs.get("failure-reason"), "provider stream was malformed");
    assert.equal(
      JSON.parse(core.outputs.get("diagnostics")).streamStructuralViolation,
      "post-finish",
    );
    assert.equal(JSON.parse(core.outputs.get("diagnostics")).postFinishShape, "delta-content");
    assert.deepEqual(
      core.events.filter((event) => event[0] === "info").map((event) => JSON.parse(event[1]))
        .find((event) => event.event === "openai-agent.failure"),
      {
        event: "openai-agent.failure",
        phase: "runtime",
        reason: "provider stream was malformed",
        category: "provider-response",
        retryable: true,
        streamStructuralViolation: "post-finish",
        postFinishShape: "delta-content",
      },
    );
    assert.doesNotMatch(JSON.stringify(core.events), new RegExp(sentinel));
  } finally {
    workspace.cleanup();
  }
});

async function withProviderFetch(responses, run) {
  const original = globalThis.fetch;
  let calls = 0;
  globalThis.fetch = async () => {
    const next = responses[calls++];
    return typeof next === "function" ? next() : next;
  };
  try {
    await run();
  } finally {
    globalThis.fetch = original;
  }
  return calls;
}

function sseBody(chunks) {
  return new Response(`${chunks.map((choices) => `data: ${JSON.stringify({
    id: "completion", object: "chat.completion.chunk", created: 0, model: "test", choices,
  })}\n\n`).join("")}data: [DONE]\n\n`, {
    status: 200,
    headers: { "content-type": "text/event-stream" },
  });
}

function retryingActionFixture() {
  const workspace = actionFixture();
  const config = JSON.parse(fs.readFileSync(path.join(workspace.directory, "config.json"), "utf8"));
  write(workspace.directory, "config.json", JSON.stringify({ ...config, max_request_retries: 1 }));
  return workspace;
}

const malformedPostFinishStream = () => sseBody([
  [{ index: 0, delta: { content: '{"answer":"MODEL_STREAM_SECRET_SENTINEL"}' }, finish_reason: "stop" }],
  [{ index: 0, delta: { content: "" } }],
]);

test("main reports a malformed stream that a request retry recovered", async () => {
  const workspace = retryingActionFixture();
  const core = mockCore({
    "api-key": "key",
    "base-url": "https://provider.example/v1",
    "config-file": "config.json",
  });
  try {
    const calls = await withProviderFetch([
      malformedPostFinishStream,
      () => sseBody([[{ index: 0, delta: { content: '{"answer":"done"}' }, finish_reason: "stop" }]]),
    ], () => main(core, { GITHUB_WORKSPACE: workspace.directory }));
    assert.equal(calls, 2);
    assert.equal(core.outputs.get("failure-reason"), "");
    assert.equal(core.outputs.get("structured-output"), '{"answer":"done"}');
    assert.equal(core.outputs.get("turn-count"), "1");
    const diagnostics = JSON.parse(core.outputs.get("diagnostics"));
    assert.equal(diagnostics.requestRetryCount, 1);
    assert.deepEqual(
      diagnostics.providerAttempts.map((attempt) => attempt.postFinishShape),
      ["delta-content-empty", undefined],
    );
    const completion = core.events.filter((event) => event[0] === "info")
      .map((event) => JSON.parse(event[1]))
      .find((event) => event.event === "openai-agent.complete");
    assert.equal(completion.requestRetryCount, 1);
    assert.equal(completion.streamStructuralViolation, "post-finish");
    assert.equal(completion.postFinishShape, "delta-content-empty");
    assert.doesNotMatch(JSON.stringify(core.events), /MODEL_STREAM_SECRET_SENTINEL/);
  } finally {
    workspace.cleanup();
  }
});

test("main logs only the violation that caused the terminal failure", async () => {
  const workspace = retryingActionFixture();
  const core = mockCore({
    "api-key": "key",
    "base-url": "https://provider.example/v1",
    "config-file": "config.json",
  });
  try {
    await withProviderFetch([
      malformedPostFinishStream,
      () => new Response("{}", { status: 401, headers: { "content-type": "application/json" } }),
    ], () => main(core, { GITHUB_WORKSPACE: workspace.directory }));
    assert.equal(core.outputs.get("failure-reason"), "provider credential rejected");
    assert.equal(
      JSON.parse(core.outputs.get("diagnostics")).streamStructuralViolation,
      "post-finish",
    );
    assert.deepEqual(
      core.events.filter((event) => event[0] === "info").map((event) => JSON.parse(event[1]))
        .find((event) => event.event === "openai-agent.provider-failure"),
      {
        event: "openai-agent.provider-failure",
        reason: "provider credential rejected",
        category: "provider-credential",
        retryable: false,
        status: 401,
      },
    );
  } finally {
    workspace.cleanup();
  }
});

test("main reduces malformed SSE parser errors to a static structural violation", async () => {
  const workspace = actionFixture();
  const core = mockCore({
    "api-key": "key",
    "base-url": "https://provider.example/v1",
    "config-file": "config.json",
  });
  const sentinel = "RAW_SSE_SECRET_SENTINEL";
  class MalformedSseOpenAI {
    constructor() {
      this.chat = { completions: { create: async () => {
        throw new SyntaxError(sentinel);
      } } };
    }
  }
  try {
    await main(core, { GITHUB_WORKSPACE: workspace.directory }, MalformedSseOpenAI);
    assert.equal(core.outputs.get("failure-reason"), "provider stream was malformed");
    assert.equal(
      JSON.parse(core.outputs.get("diagnostics")).streamStructuralViolation,
      "sse-json-invalid",
    );
    assert.deepEqual(
      core.events.filter((event) => event[0] === "info").map((event) => JSON.parse(event[1]))
        .find((event) => event.event === "openai-agent.provider-failure"),
      {
        event: "openai-agent.provider-failure",
        reason: "provider stream was malformed",
        category: "provider-response",
        retryable: true,
        streamStructuralViolation: "sse-json-invalid",
      },
    );
    assert.doesNotMatch(JSON.stringify(core.events), new RegExp(sentinel));
  } finally {
    workspace.cleanup();
  }
});

test("main reports why repaired output remains invalid", async () => {
  const workspace = actionFixture();
  const core = mockCore({
    "api-key": "key",
    "base-url": "https://provider.example/v1",
    "config-file": "config.json",
  });
  const responses = [
    { choices: [{ message: { content: '{"wrong":true}' } }] },
    { choices: [{ message: { content: "not-json" } }] },
  ];
  class InvalidRepairOpenAI {
    constructor() {
      this.chat = { completions: { create: async () => completionStream(responses.shift()) } };
    }
  }
  const reason =
    "output remained invalid after the repair limit: json: response was not valid JSON";
  try {
    await main(core, { GITHUB_WORKSPACE: workspace.directory }, InvalidRepairOpenAI);
    assert.equal(core.outputs.get("structured-output"), "");
    assert.equal(core.outputs.get("failure-reason"), reason);
    assert.equal(core.outputs.get("failure-category"), "output-invalid");
    assert.equal(core.outputs.get("retryable"), "false");
    assert.equal(core.outputs.get("turn-count"), "2");
    assert.deepEqual(JSON.parse(core.outputs.get("diagnostics")).outputRejections, [
      {
        attempt: 1,
        activity: "investigating",
        layer: "schema",
        reason: "response did not match the schema: #: required answer; #: has an unexpected property",
      },
      { attempt: 2, activity: "repairing", layer: "json", reason: "response was not valid JSON" },
    ]);
    assert.deepEqual(
      core.events.filter((event) => event[0] === "failed").map((event) => event[1]),
      [reason],
    );
    assert.deepEqual(
      core.events.filter((event) => event[0] === "info").map((event) => JSON.parse(event[1]))
        .find((event) => event.event === "openai-agent.failure"),
      {
        event: "openai-agent.failure",
        phase: "runtime",
        reason,
        category: "output-invalid",
        retryable: false,
      },
    );
  } finally {
    workspace.cleanup();
  }
});

test("review rejection diagnostics and failure logs never echo finding text", async () => {
  const automation = path.join(__dirname, "fixtures", "review");
  const secret = "model-secret-sentinel";
  const sha = "a".repeat(40);
  const finding = {
    question: false, severity: "low", path: `src/${secret}.cs`,
    start_line: null, end_line: null, title: secret, rationale: secret, confidence: 0.5,
  };
  for (const scenario of ["specialist", "general", "preservation"]) {
    const workspace = actionFixture();
    const general = scenario === "general";
    const candidate = {
      head_sha: sha, summary: "review",
      ...(general ? { candidate_dispositions: [] } : { reviewer: "skeptical" }),
      findings: [{ ...finding, ...(general ? { sources: [] } : { id: secret, references: [] }) }],
    };
    const metadata = {
      stage: general ? "general" : "specialist",
      reviewer: "skeptical", expected_sha: sha,
      validation_context_file: write(workspace.directory, "context.json", JSON.stringify({
        changed_paths: ["src/Library.cs"], changed_lines: {},
      })),
      aggregate_file: write(workspace.directory, "aggregate.json", JSON.stringify({
        head_sha: sha, reviewers: [],
      })),
    };
    write(workspace.directory, "schema.json", fs.readFileSync(path.join(
      automation, "schemas", general ? "final-review.json" : "candidate-review.json",
    ), "utf8"));
    write(workspace.directory, "validator.js",
      `exports.validate = require(${JSON.stringify(path.join(automation, "agent-validator.js"))})` +
      `.${general ? "validateGeneral" : "validateSpecialist"};`);
    const core = mockCore({
      "api-key": "key",
      "base-url": "https://provider.example/v1",
      "config-file": "config.json",
      validator: "validator.js#validate",
      "validator-metadata": JSON.stringify(metadata),
    });
    const responses = [candidate, scenario === "preservation" ? { ...candidate, findings: [] } : candidate];
    class InvalidReviewOpenAI {
      constructor() {
        this.chat = { completions: { create: async () => completionStream({
          choices: [{ message: { content: JSON.stringify(responses.shift()) } }],
        }) } };
      }
    }
    try {
      await main(core, { GITHUB_WORKSPACE: workspace.directory }, InvalidReviewOpenAI);
      assert.equal(core.outputs.get("structured-output"), "");
      assert.equal(core.outputs.get("failure-category"), "output-invalid");
      const reason = core.outputs.get("failure-reason");
      assert.match(reason, scenario === "preservation"
        ? /restore the missing findings/
        : /finding at index 0 must cite a path changed/);
      const rejections = JSON.parse(core.outputs.get("diagnostics")).outputRejections;
      assert.equal(rejections.length, 2);
      assert.ok(rejections.every((entry) => entry.layer === "semantic"));
      const emitted = core.events.filter(([kind]) => ["output", "info", "failed"].includes(kind));
      assert.ok(!JSON.stringify(emitted).includes(secret), scenario);
    } finally {
      workspace.cleanup();
    }
  }
});

// A self-contained review validator, schema, and trusted fixtures, through the real runtime:
// a rejection has to reach the provider as actionable feedback, and one corrected response has to
// be enough to account for every candidate the first answer left out.
function reviewFixture(workspace, { candidates = 4 } = {}) {
  const automation = path.join(__dirname, "fixtures", "review");
  const sha = "a".repeat(40);
  const secret = "model-secret-sentinel";
  const aggregate = {
    head_sha: sha,
    reviewers: [
      { reviewer: "dotnet", status: "valid", summary: "no runtime defect", findings: [] },
      {
        reviewer: "skeptical", status: "valid", summary: "candidate review",
        findings: Array.from({ length: candidates }, (_, index) => ({
          id: `${secret}-${index + 1}`, question: false, severity: "high", path: "src/Library.cs",
          start_line: 4, end_line: 4, title: `${secret} title`, rationale: `${secret} rationale`,
          confidence: 0.9, references: [],
        })),
      },
      { reviewer: "code-compressor", status: "failed", reason: "provider request timed out" },
    ],
  };
  write(workspace.directory, "schema.json",
    fs.readFileSync(path.join(automation, "schemas", "final-review.json"), "utf8"));
  write(workspace.directory, "validator.js",
    `exports.validate = require(${JSON.stringify(path.join(automation, "agent-validator.js"))})` +
    ".validateGeneral;");
  const context = {
    changed_paths: ["src/Library.cs"], changed_lines: { "src/Library.cs": [4] },
  };
  const metadata = {
    stage: "general",
    expected_sha: sha,
    validation_context_file: write(workspace.directory, "context.json", JSON.stringify(context)),
    aggregate_file: write(workspace.directory, "aggregate.json", JSON.stringify(aggregate)),
  };
  return {
    automation, sha, secret, context,
    core: () => mockCore({
      "api-key": "key",
      "base-url": "https://provider.example/v1",
      "config-file": "config.json",
      validator: "validator.js#validate",
      "validator-metadata": JSON.stringify(metadata),
    }),
    review: (covered) => ({
      head_sha: sha,
      summary: "verified",
      candidate_dispositions: Array.from({ length: covered }, (_, index) => ({
        reviewer: "skeptical", finding_id: `${secret}-${index + 1}`,
        disposition: "accepted", rationale: `${secret} holds up`,
      })),
      findings: [{
        question: false, severity: "high", path: "src/Library.cs", start_line: 4, end_line: 4,
        title: `${secret} boundary defect`, rationale: `${secret} verified`, confidence: 0.95,
        sources: Array.from({ length: covered }, (_, index) => ({
          reviewer: "skeptical", finding_id: `${secret}-${index + 1}`,
        })),
      }],
    }),
    // Schema-valid in every field, and wrong in four different ways at once: a duplicate, an
    // unknown candidate, a forbidden control character, and every remaining candidate left out.
    mixed: () => ({
      head_sha: sha,
      summary: "verified",
      candidate_dispositions: [
        { reviewer: "skeptical", finding_id: `${secret}-1`, disposition: "rejected", rationale: "unsupported" },
        { reviewer: "skeptical", finding_id: `${secret}-1`, disposition: "rejected", rationale: "unsupported again" },
        { reviewer: "skeptical", finding_id: "ghost-candidate", disposition: "rejected", rationale: "unsupported" },
        { reviewer: "skeptical", finding_id: `${secret}-2`, disposition: "rejected", rationale: "unsupported\u0000" },
      ],
      findings: [],
    }),
    // The authoritative rejection for a given output, so a test can require the runtime to carry
    // exactly it rather than merely something that looks like it.
    rejectionFor: (output) => require(path.join(automation, "validate-final-review"))
      .validateFinalReview(output, {
        expectedSha: sha,
        changedPaths: context.changed_paths,
        changedLines: context.changed_lines,
        specialistAggregate: aggregate,
      }),
    reasonFor: (output) => require(path.join(automation, "validate-final-review"))
      .validateFinalReview(output, {
        expectedSha: sha,
        changedPaths: context.changed_paths,
        changedLines: context.changed_lines,
        specialistAggregate: aggregate,
      }).reason,
  };
}

function mockProvider(responses, requests) {
  return class ReviewOpenAI {
    constructor() {
      this.chat = { completions: { create: async (request) => {
        // The runtime appends to one conversation, so a request is only readable afterwards if the
        // messages it carried are copied as they were sent.
        requests.push({ ...request, messages: request.messages.map((message) => ({ ...message })) });
        return completionStream({
          choices: [{ message: { content: JSON.stringify(responses.shift()) } }],
        });
      } } };
    }
  };
}

test("a final review missing four dispositions is repaired from one factual rejection", async () => {
  const workspace = actionFixture();
  const fixture = reviewFixture(workspace);
  const core = fixture.core();
  const requests = [];
  try {
    // This fixture affords one repair, which is enough to show a single rejection converging. The
    // upstream integration fixture afforded two; retain those historical budget assertions.
    const shipped = JSON.parse(fs.readFileSync(
      path.join(fixture.automation, "agents", "general-reviewer.json"), "utf8"));
    assert.equal(shipped.max_output_repair_attempts, 2);
    assert.equal(shipped.max_turns, 32);
    assert.equal(shipped.max_tool_calls, 120);
    assert.equal(shipped.max_request_retries, 4);

    await main(core, { GITHUB_WORKSPACE: workspace.directory },
      mockProvider([fixture.review(1), fixture.review(4)], requests));

    // The rejection the provider was asked to repair names every candidate left out, at positions
    // in the trusted aggregate and by the identifiers to copy, and carries no instruction the
    // reviewer prompt already states. Only the candidates still missing are quoted.
    const repairRequest = requests[1].messages.at(-1).content;
    assert.match(repairRequest, /3 of 4 candidates have no valid disposition/);
    assert.match(repairRequest, /aggregate findings skeptical 1, 2, 3/);
    for (const index of [2, 3, 4]) {
      assert.ok(repairRequest.includes(`(skeptical, ${fixture.secret}-${index})`), repairRequest);
    }
    assert.doesNotMatch(repairRequest, /record exactly one disposition per specialist candidate/);
    assert.ok(!repairRequest.includes(`${fixture.secret}-1`), repairRequest);

    // One corrected response accounted for all four, inside the unchanged repair budget.
    assert.equal(requests.length, 2);
    assert.equal(core.outputs.get("failure-reason"), "");
    assert.equal(core.outputs.get("turn-count"), "2");
    assert.equal(core.events.some((event) => event[0] === "failed"), false);
    const diagnostics = JSON.parse(core.outputs.get("diagnostics"));
    assert.equal(diagnostics.outputRepairCount, 1);
    assert.deepEqual(diagnostics.outputRejections.map((entry) => entry.layer), ["semantic"]);
    assert.match(diagnostics.outputRejections[0].reason, /3 of 4 candidates have no valid disposition/);

    // The published review is what the pipeline's own validator derives from that output.
    const {
      provenancePrefix, validateFinalReview,
    } = require(path.join(fixture.automation, "validate-final-review"));
    const validated = validateFinalReview(core.outputs.get("structured-output"), {
      expectedSha: fixture.sha,
      changedPaths: fixture.context.changed_paths,
      changedLines: fixture.context.changed_lines,
      specialistAggregate: JSON.parse(fs.readFileSync(
        path.join(workspace.directory, "aggregate.json"), "utf8")),
    });
    assert.equal(validated.ok, true, validated.reason);
    assert.deepEqual(Object.keys(validated.value), ["head_sha", "summary", "findings"]);
    assert.equal(provenancePrefix(validated.value.findings[0].sources), "[skeptical]");
    assert.equal(validated.value.findings[0].sources.length, 4);

    // Accepted model text belongs in the output, never in the logs.
    const logs = JSON.stringify(core.events.filter(([kind]) => kind === "info" || kind === "failed"));
    assert.ok(!logs.includes(fixture.secret), logs);
  } finally {
    workspace.cleanup();
  }
});

test("a final review that never accounts for its candidates exhausts repair with the same fact", async () => {
  const workspace = actionFixture();
  const fixture = reviewFixture(workspace);
  const core = fixture.core();
  const requests = [];
  try {
    await main(core, { GITHUB_WORKSPACE: workspace.directory },
      mockProvider([fixture.review(1), fixture.review(2)], requests));

    assert.equal(core.outputs.get("structured-output"), "");
    assert.equal(core.outputs.get("failure-category"), "output-invalid");
    assert.equal(core.outputs.get("turn-count"), "2");
    // The runtime slices both its telemetry reason and `semantic: <reason>` at 240 bytes, so the
    // only useful assertion is that what it published is the validator's reason entire.
    const expected = fixture.reasonFor(fixture.review(2));
    assert.match(expected, /2 of 4 candidates have no valid disposition/);
    const reason = core.outputs.get("failure-reason");
    assert.equal(reason, `output remained invalid after the repair limit: semantic: ${expected}`);
    assert.doesNotMatch(reason, /record exactly one disposition per specialist candidate/);
    const diagnostics = JSON.parse(core.outputs.get("diagnostics"));
    assert.equal(diagnostics.outputRepairCount, 1);
    assert.equal(diagnostics.outputRejections.length, 2);
    assert.equal(diagnostics.outputRejections[1].reason, expected);
    const emitted = core.events.filter(([kind]) => ["output", "info", "failed"].includes(kind));
    assert.ok(!JSON.stringify(emitted).includes(fixture.secret), JSON.stringify(emitted));
  } finally {
    workspace.cleanup();
  }
});

// The longest diagnostics the validator produces are the mixed ones, and they are exactly the ones
// a byte slice would quietly rob of a category, a count, or a limit.
test("a review wrong in several ways at once keeps every category through the runtime", async () => {
  const workspace = actionFixture();
  const fixture = reviewFixture(workspace, { candidates: 20 });
  const core = fixture.core();
  const requests = [];
  try {
    await main(core, { GITHUB_WORKSPACE: workspace.directory },
      mockProvider([fixture.mixed(), fixture.mixed()], requests));

    const expected = fixture.reasonFor(fixture.mixed());
    // Every failure the output contains is accounted for, with its own count.
    assert.match(expected, /candidates lack a valid disposition|candidates have no valid disposition/);
    assert.match(expected, /1 unknown|1 entry naming a candidate the specialists did not report/);
    assert.match(expected, /1 duplicate|1 entry repeating a candidate an earlier entry already covered/);
    assert.match(expected, /forbidden-control/);

    // The repair prompt carries bounded validation feedback without model text.
    assert.match(requests[1].messages.at(-1).content, /Correct every reported validation error/);
    const diagnostics = JSON.parse(core.outputs.get("diagnostics"));
    assert.deepEqual(diagnostics.outputRejections.map((entry) => entry.reason), [expected, expected]);
    assert.equal(core.outputs.get("failure-reason"),
      `output remained invalid after the repair limit: semantic: ${expected}`);
    assert.ok(!core.outputs.get("failure-reason").includes(fixture.secret));
  } finally {
    workspace.cleanup();
  }
});

test("one repair corrects duplicate and rationale failures in either entry order", async () => {
  for (const invalidIndexes of [[0], [1], [0, 1]]) {
    const workspace = actionFixture();
    const fixture = reviewFixture(workspace, { candidates: 1 });
    const core = fixture.core();
    const requests = [];
    try {
      const invalid = fixture.review(1);
      invalid.candidate_dispositions.push({ ...invalid.candidate_dispositions[0] });
      for (const index of invalidIndexes) invalid.candidate_dispositions[index].rationale = " ";
      const rejection = fixture.rejectionFor(invalid);
      const expected = rejection.reason;
      assert.match(expected, /1 duplicate|1 entry repeating/);
      assert.match(expected, new RegExp(`${invalidIndexes.length} (?:entr(?:y|ies) )?with a`));

      await main(core, { GITHUB_WORKSPACE: workspace.directory },
        mockProvider([invalid, fixture.review(1)], requests));

      // Repair is asked with the full detail, which keeps every coordinate the short reason dropped.
      assert.equal(requests.length, 2);
      const feedback = rejection.detail ?? expected;
      assert.ok(requests[1].messages.at(-1).content.includes(feedback), feedback);
      assert.equal(core.outputs.get("failure-reason"), "");
      assert.deepEqual(JSON.parse(core.outputs.get("structured-output")), fixture.review(1));
      const diagnostics = JSON.parse(core.outputs.get("diagnostics"));
      assert.equal(diagnostics.outputRepairCount, 1);
      assert.deepEqual(diagnostics.outputRejections.map((entry) => entry.reason), [expected]);
      const logs = JSON.stringify(core.events.filter(([kind]) => kind === "info" || kind === "failed"));
      assert.ok(!logs.includes(fixture.secret), logs);
    } finally {
      workspace.cleanup();
    }
  }
});

test("main reports configuration failures without constructing a provider client", async () => {
  const workspace = actionFixture();
  const core = mockCore({
    "api-key": "key",
    "base-url": "https://provider.example/v1",
    "config-file": "../config.json",
  });
  let constructed = false;
  class UnexpectedOpenAI {
    constructor() {
      constructed = true;
    }
  }
  try {
    await main(core, { GITHUB_WORKSPACE: workspace.directory }, UnexpectedOpenAI);
    assert.equal(constructed, false);
    assert.equal(core.outputs.get("failure-reason"), "invalid path");
    assert.equal(core.events.some((event) => event[0] === "failed"), true);
    assert.deepEqual(
      core.events.filter((event) => event[0] === "info").map((event) => JSON.parse(event[1]))
        .find((event) => event.event === "openai-agent.failure"),
      {
        event: "openai-agent.failure",
        phase: "configuration",
        reason: "invalid path",
        category: "configuration",
        retryable: false,
      },
    );
  } finally {
    workspace.cleanup();
  }
});

test("main reports each missing input without constructing a provider client", async () => {
  const workspace = actionFixture();
  const cases = [
    [{}, "api key input is missing"],
    [{ "api-key": "key" }, "base URL input is missing"],
    [{
      "api-key": "key",
      "base-url": "https://provider.example/v1",
    }, "config file input is missing"],
  ];
  try {
    for (const [inputs, reason] of cases) {
      const core = mockCore(inputs);
      let constructed = false;
      class UnexpectedOpenAI {
        constructor() {
          constructed = true;
        }
      }
      await main(core, { GITHUB_WORKSPACE: workspace.directory }, UnexpectedOpenAI);
      assert.equal(constructed, false);
      assert.equal(core.outputs.get("failure-reason"), reason);
      assert.deepEqual(
        core.events.filter((event) => event[0] === "info").map((event) => JSON.parse(event[1]))
          .find((event) => event.event === "openai-agent.failure"),
        {
          event: "openai-agent.failure",
          phase: "input",
          reason,
          category: "input",
          retryable: false,
        },
      );
    }
  } finally {
    workspace.cleanup();
  }
});

test("main reports workspace and provider client initialization failures safely", async () => {
  const workspace = actionFixture();
  const inputs = {
    "api-key": "key",
    "base-url": "https://provider.example/v1",
    "config-file": "config.json",
  };
  try {
    const missingWorkspaceCore = mockCore(inputs);
    await main(missingWorkspaceCore, {}, class UnexpectedOpenAI {});
    assert.equal(missingWorkspaceCore.outputs.get("failure-reason"), "workspace is unavailable");
    assert.deepEqual(
      missingWorkspaceCore.events.filter((event) => event[0] === "info")
        .map((event) => JSON.parse(event[1]))
        .find((event) => event.event === "openai-agent.failure"),
      {
        event: "openai-agent.failure",
        phase: "configuration",
        reason: "workspace is unavailable",
        category: "configuration",
        retryable: false,
      },
    );

    const initializationCore = mockCore(inputs);
    class FailingOpenAI {
      constructor() {
        throw new Error("CLIENT_INITIALIZATION_SECRET_SENTINEL");
      }
    }
    await main(initializationCore, { GITHUB_WORKSPACE: workspace.directory }, FailingOpenAI);
    assert.equal(
      initializationCore.outputs.get("failure-reason"),
      "provider client initialization failed",
    );
    assert.deepEqual(
      initializationCore.events.filter((event) => event[0] === "info")
        .map((event) => JSON.parse(event[1]))
        .find((event) => event.event === "openai-agent.failure"),
      {
        event: "openai-agent.failure",
        phase: "initialization",
        reason: "provider client initialization failed",
        category: "configuration",
        retryable: false,
      },
    );
    assert.doesNotMatch(
      JSON.stringify(initializationCore.events),
      /CLIENT_INITIALIZATION_SECRET_SENTINEL/,
    );
  } finally {
    workspace.cleanup();
  }
});
