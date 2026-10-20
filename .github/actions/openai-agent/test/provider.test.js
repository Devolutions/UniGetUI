"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const OpenAI = require("openai");

const {
  ResponseBodySizeError, RuntimeMetrics, createProviderClient, createResponseBodyMonitor,
  providerErrorCode, retryAfterMilliseconds,
} = require("../src/provider");

class BaseClient {
  constructor(options) {
    this.options = options;
  }

  async shouldRetry() {
    return true;
  }

  async retryRequest(options, retriesRemaining, requestLogID) {
    return this.makeRequest(options, retriesRemaining - 1, requestLogID);
  }
}

test("provider error codes use bounded documented locations", () => {
  assert.equal(providerErrorCode({ code: "top_level" }), "top_level");
  assert.equal(providerErrorCode({
    provider_specific_fields: { code: "surface_not_credit_eligible" },
  }), "surface_not_credit_eligible");
  assert.equal(providerErrorCode({
    code: "503",
    error: { provider_specific_fields: { code: "surface_not_credit_eligible" } },
  }), "surface_not_credit_eligible");
  assert.equal(providerErrorCode({ detail: { code: "metering_unavailable" } }), "metering_unavailable");
  assert.equal(providerErrorCode({ body: { error: { code: "monthly_cap_reached" } } }), "monthly_cap_reached");
  assert.equal(providerErrorCode({ code: "unsafe value" }), undefined);
});

test("provider client disables SDK retry ownership", () => {
  const client = createProviderClient(
    BaseClient,
    { maxRetries: 9 },
    new RuntimeMetrics(),
  );
  assert.equal(client.options.maxRetries, 0);
});

test("Retry-After parsing accepts bounded syntax without clipping the stage policy", () => {
  assert.equal(retryAfterMilliseconds(new Headers({ "retry-after-ms": "250" })), 250);
  assert.equal(
    retryAfterMilliseconds(new Headers({ "retry-after-ms": "3000000000" })),
    3_000_000_000,
  );
  assert.equal(
    retryAfterMilliseconds(
      new Headers({ "retry-after": "Thu, 01 Jan 1970 00:10:00 GMT" }),
      0,
    ),
    600_000,
  );
  assert.equal(retryAfterMilliseconds(new Headers({ "retry-after": "invalid" })), undefined);
});

test("response monitor counts raw body bytes before parsing", async () => {
  const monitor = createResponseBodyMonitor({ idleTimeoutMs: 1000, maximumBytes: 4 });
  let aborted = false;
  monitor.beginAttempt(() => { aborted = true; });
  const response = monitor.wrap(new Response(new Uint8Array([1, 2, 3, 4, 5])));
  await assert.rejects(
    response.arrayBuffer(),
    (error) => error instanceof ResponseBodySizeError,
  );
  assert.equal(aborted, true);
  assert.equal(monitor.failure instanceof ResponseBodySizeError, true);
});

test("stream structural diagnostics use a closed first-write-wins vocabulary", () => {
  const metrics = new RuntimeMetrics();
  metrics.recordStreamStructuralViolation("MODEL_STREAM_SECRET_SENTINEL");
  assert.equal(metrics.snapshot().streamStructuralViolation, undefined);
  metrics.recordStreamStructuralViolation("choice-index-invalid");
  metrics.recordStreamStructuralViolation("tool-call-index-mixed");
  assert.equal(metrics.snapshot().streamStructuralViolation, "choice-index-invalid");
});

test("post-finish diagnostics use closed shapes and saturating counters", () => {
  const metrics = new RuntimeMetrics();
  metrics.recordPostFinishShape("POST_FINISH_STREAM_SECRET_SENTINEL");
  metrics.recordPostFinishShape("delta-content");
  metrics.recordPostFinishShape("delta-extension");
  metrics.recordIgnoredPostFinishEmptyDeltaChoice(false);
  metrics.recordIgnoredPostFinishEmptyDeltaChoice(true);
  assert.deepEqual({
    ignoredPostFinishEmptyDeltaChoices: metrics.snapshot().ignoredPostFinishEmptyDeltaChoices,
    ignoredRepeatedTerminalChoices: metrics.snapshot().ignoredRepeatedTerminalChoices,
    postFinishShape: metrics.snapshot().postFinishShape,
  }, {
    ignoredPostFinishEmptyDeltaChoices: 2,
    ignoredRepeatedTerminalChoices: 1,
    postFinishShape: "delta-content",
  });

  metrics.ignoredPostFinishEmptyDeltaChoices = Number.MAX_SAFE_INTEGER;
  metrics.ignoredRepeatedTerminalChoices = Number.MAX_SAFE_INTEGER;
  metrics.recordIgnoredPostFinishEmptyDeltaChoice(true);
  assert.equal(metrics.snapshot().ignoredPostFinishEmptyDeltaChoices, Number.MAX_SAFE_INTEGER);
  assert.equal(metrics.snapshot().ignoredRepeatedTerminalChoices, Number.MAX_SAFE_INTEGER);
  assert.doesNotMatch(JSON.stringify(metrics.snapshot()), /POST_FINISH_STREAM_SECRET_SENTINEL/);
});

test("runtime metrics retain activity and mark partial usage incomplete", async () => {
  const metrics = new RuntimeMetrics(() => 0);
  const requestBody = { model: "test", messages: [{ role: "user", content: "prompt" }] };
  const request = metrics.beginRequest("repairing", requestBody);
  const first = metrics.beginAttempt();
  metrics.observeResponse(first, new Response("", { status: 429 }));
  metrics.finishAttempt(first);
  const second = metrics.beginAttempt();
  metrics.observeResponse(second, new Response("", {
    status: 200,
    headers: { "x-request-id": "req_safe-123" },
  }));
  metrics.finishAttempt(second);
  metrics.recordCompletion(request, {
    choices: [{ finish_reason: "stop" }],
    usage: { prompt_tokens: 4, completion_tokens: 3, total_tokens: 7 },
  });

  const snapshot = metrics.snapshot();
  assert.equal(snapshot.requestRetryCount, 1);
  assert.equal(snapshot.providerFinishReason, "stop");
  assert.deepEqual(snapshot.tokenUsage, {
    complete: false,
    knownAttemptCount: 1,
    unknownAttemptCount: 1,
    inputTokens: 4,
    outputTokens: 3,
    totalTokens: 7,
  });
  assert.deepEqual(snapshot.providerAttempts, [
    {
      activity: "repairing",
      logicalCall: 1,
      attempt: 1,
      messageCount: 1,
      requestBytes: Buffer.byteLength(JSON.stringify(requestBody), "utf8"),
      toolResultBytes: 0,
      durationMs: 0,
      status: 429,
    },
    {
      activity: "repairing",
      logicalCall: 1,
      attempt: 2,
      messageCount: 1,
      requestBytes: Buffer.byteLength(JSON.stringify(requestBody), "utf8"),
      toolResultBytes: 0,
      durationMs: 0,
      status: 200,
      requestId: "req_safe-123",
      finishReason: "stop",
      usage: { inputTokens: 4, outputTokens: 3, totalTokens: 7 },
    },
  ]);
});

test("runtime metrics index logical calls and accumulate tool-result bytes without content", () => {
  const metrics = new RuntimeMetrics(() => 0);
  const first = metrics.beginRequest("investigating", {
    model: "test", messages: [{ role: "system", content: "instructions" }],
  });
  const firstAttempt = metrics.beginAttempt();
  metrics.finishAttempt(firstAttempt);
  metrics.recordCompletion(first, { choices: [{ finish_reason: "tool_calls" }] });
  metrics.recordToolResult('{"ok":true}');

  const secondBody = {
    model: "test",
    messages: [
      { role: "system", content: "instructions" },
      { role: "assistant", content: null, tool_calls: [] },
      { role: "tool", tool_call_id: "tool", content: '{"ok":true}' },
    ],
  };
  const second = metrics.beginRequest("finalizing", secondBody);
  const secondAttempt = metrics.beginAttempt();
  metrics.finishAttempt(secondAttempt);
  metrics.recordCompletion(second, { choices: [{ finish_reason: "stop" }] });

  assert.deepEqual(metrics.snapshot().providerAttempts.map((attempt) => ({
    logicalCall: attempt.logicalCall,
    attempt: attempt.attempt,
    messageCount: attempt.messageCount,
    requestBytes: attempt.requestBytes,
    toolResultBytes: attempt.toolResultBytes,
  })), [
    {
      logicalCall: 1,
      attempt: 1,
      messageCount: 1,
      requestBytes: Buffer.byteLength(JSON.stringify({
        model: "test", messages: [{ role: "system", content: "instructions" }],
      }), "utf8"),
      toolResultBytes: 0,
    },
    {
      logicalCall: 2,
      attempt: 2,
      messageCount: 3,
      requestBytes: Buffer.byteLength(JSON.stringify(secondBody), "utf8"),
      toolResultBytes: Buffer.byteLength('{"ok":true}', "utf8"),
    },
  ]);
  assert.doesNotMatch(JSON.stringify(metrics.snapshot()), /instructions|tool_call_id/);
});

test("runtime metrics mark individual missing usage fields incomplete", () => {
  const metrics = new RuntimeMetrics(() => 0);
  const first = metrics.beginRequest("investigating");
  const firstAttempt = metrics.beginAttempt();
  metrics.finishAttempt(firstAttempt);
  metrics.recordCompletion(first, {
    choices: [{ finish_reason: "stop" }],
    usage: { prompt_tokens: 4, completion_tokens: 3, total_tokens: 7 },
  });
  const second = metrics.beginRequest("investigating");
  const secondAttempt = metrics.beginAttempt();
  metrics.finishAttempt(secondAttempt);
  metrics.recordCompletion(second, {
    choices: [{ finish_reason: "stop" }],
    usage: { prompt_tokens: 6 },
  });

  assert.deepEqual(metrics.snapshot().tokenUsage, {
    complete: false,
    knownAttemptCount: 2,
    unknownAttemptCount: 0,
    inputTokens: 10,
    outputTokens: 3,
    totalTokens: 7,
  });
});

test("provider attempts include full response-body consumption time", async () => {
  const metrics = new RuntimeMetrics();
  const client = createProviderClient(
    BaseClient,
    {},
    metrics,
    async () => new Response(new ReadableStream({
      start(controller) {
        setTimeout(() => {
          controller.enqueue(new TextEncoder().encode("{}"));
          controller.close();
        }, 20);
      },
    }), { status: 200 }),
  );
  const request = metrics.beginRequest("investigating");
  const response = await client.options.fetch("https://provider.example/v1");
  assert.equal(metrics.snapshot().providerAttempts[0].durationMs < 20, true);
  await response.text();
  metrics.recordCompletion(request, { choices: [{ finish_reason: "stop" }] });
  assert.equal(metrics.snapshot().providerAttempts[0].durationMs >= 20, true);
});
