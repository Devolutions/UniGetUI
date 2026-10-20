"use strict";

const {
  MAX_OUTPUT_REJECTION_REASON_BYTES, MAX_OUTPUT_REJECTIONS, MAX_VALIDATION_DETAIL_BYTES,
} = require("./limits");

const SAFE_DIAGNOSTIC_VALUE = /^[A-Za-z0-9._:-]{1,128}$/;
// Rejection reasons are assembled from the trusted output schema and from validator text that the
// validator loader has already restricted, so this only bounds what a future caller could add.
const UNSAFE_REASON_CHARACTER = /[^A-Za-z0-9 #.,:;()/_-]+/g;
const RESPONSE_BODY_MONITOR = Symbol("response-body-monitor");
const MAX_TIMER_DELAY_MS = 2_147_483_647;
const STREAM_STRUCTURAL_VIOLATIONS = new Set([
  "choice-delta-invalid",
  "choice-index-invalid",
  "choice-index-missing-multiple",
  "choice-invalid",
  "choice-missing",
  "chunk-invalid",
  "delta-value-invalid",
  "finish-reason-invalid",
  "finish-tool-call-mismatch",
  "post-finish",
  "role-invalid",
  "sse-json-invalid",
  "stream-structure-invalid",
  "tool-call-function-invalid",
  "tool-call-index-gap",
  "tool-call-index-invalid",
  "tool-call-index-missing-fragmented",
  "tool-call-index-missing-multiple",
  "tool-call-index-mixed",
  "tool-call-invalid",
  "tool-calls-invalid",
]);
const POST_FINISH_SHAPES = new Set([
  "choice-count",
  "choice-object",
  "choice-index",
  "finish-mismatch",
  "delta-invalid",
  "delta-content",
  "delta-content-empty",
  "delta-reasoning",
  "delta-reasoning-empty",
  "delta-tool-calls",
  "delta-extension",
]);

function providerErrorCode(error) {
  return [
    error?.provider_specific_fields?.code,
    error?.detail?.code,
    error?.error?.provider_specific_fields?.code,
    error?.body?.error?.provider_specific_fields?.code,
    error?.error?.code,
    error?.body?.error?.code,
    error?.code,
    error?.type,
  ].find((value) => typeof value === "string" && SAFE_DIAGNOSTIC_VALUE.test(value));
}

class RuntimeMetrics {
  constructor(now = Date.now) {
    this.now = now;
    this.startedAt = now();
    this.activity = "input";
    this.requests = [];
    this.activeRequest = null;
    this.outputRejections = [];
    this.attemptCount = 0;
    this.toolResultBytes = 0;
    this.providerErrorCode = undefined;
    this.streamStructuralViolation = undefined;
    this.ignoredPostFinishEmptyDeltaChoices = 0;
    this.ignoredRepeatedTerminalChoices = 0;
    this.postFinishShape = undefined;
  }

  beginRequest(activity, request) {
    this.activity = activity;
    const logicalCall = this.requests.length + 1;
    const details = {
      activity,
      logicalCall,
      messageCount: Array.isArray(request?.messages) ? request.messages.length : 0,
      requestBytes: Buffer.byteLength(JSON.stringify(request ?? {}), "utf8"),
      toolResultBytes: this.toolResultBytes,
      attempts: [],
    };
    this.requests.push(details);
    this.activeRequest = details;
    return details;
  }

  beginAttempt() {
    const attempt = {
      activity: this.activeRequest?.activity || this.activity,
      attempt: ++this.attemptCount,
      startedAt: this.now(),
    };
    this.activeRequest?.attempts.push(attempt);
    return attempt;
  }

  observeResponse(attempt, response) {
    if (response && Number.isInteger(response.status) &&
        response.status >= 100 && response.status <= 599) {
      attempt.status = response.status;
    }
    const requestId = response?.headers?.get?.("x-request-id") ||
      response?.headers?.get?.("request-id");
    if (typeof requestId === "string" && SAFE_DIAGNOSTIC_VALUE.test(requestId)) {
      attempt.requestId = requestId;
    }
  }

  finishAttempt(attempt) {
    if (!attempt || attempt.durationMs !== undefined) return;
    attempt.removeAbortListener?.();
    attempt.durationMs = Math.max(0, this.now() - attempt.startedAt);
  }

  finishActiveAttempt() {
    this.finishAttempt(this.activeRequest?.attempts.at(-1));
  }

  recordCompletion(request, response) {
    const attempt = request.attempts.at(-1);
    if (!attempt) return;
    this.finishAttempt(attempt);
    const finishReason = response?.choices?.[0]?.finish_reason;
    if (typeof finishReason === "string" && SAFE_DIAGNOSTIC_VALUE.test(finishReason)) {
      attempt.finishReason = finishReason;
    }
    const usage = normalizeUsage(response?.usage);
    if (usage) attempt.usage = usage;
  }

  recordToolResult(result) {
    this.toolResultBytes += Buffer.byteLength(String(result), "utf8");
  }

  recordProviderFailure(diagnostic) {
    const code = diagnostic?.providerCode;
    if (code !== undefined) this.providerErrorCode = code;
  }

  // The first violation and shape stay at the top level; each attempt also keeps its own so a
  // malformed attempt that a request retry recovered remains visible.
  recordStreamStructuralViolation(violation) {
    if (!STREAM_STRUCTURAL_VIOLATIONS.has(violation)) return;
    this.streamStructuralViolation ??= violation;
    const attempt = this.activeRequest?.attempts.at(-1);
    if (attempt) attempt.streamStructuralViolation ??= violation;
  }

  recordIgnoredPostFinishEmptyDeltaChoice(repeatedTerminal) {
    this.ignoredPostFinishEmptyDeltaChoices = saturatingIncrement(
      this.ignoredPostFinishEmptyDeltaChoices,
    );
    if (repeatedTerminal) {
      this.ignoredRepeatedTerminalChoices = saturatingIncrement(
        this.ignoredRepeatedTerminalChoices,
      );
    }
  }

  recordPostFinishShape(shape) {
    if (!POST_FINISH_SHAPES.has(shape)) return;
    this.postFinishShape ??= shape;
    const attempt = this.activeRequest?.attempts.at(-1);
    if (attempt) attempt.postFinishShape ??= shape;
  }

  // A rejected output attempt is the only evidence left of why a stage exhausted its repairs, so it
  // is kept as bounded telemetry rather than being reduced to the exhaustion itself. The short reason
  // alone can drop what the attempt got wrong, so a validator's full detail travels beside it.
  recordOutputRejection({ activity, layer, reason, detail }) {
    if (this.outputRejections.length >= MAX_OUTPUT_REJECTIONS) return;
    const sanitizedDetail = detail === undefined
      ? ""
      : sanitizeReason(detail, MAX_VALIDATION_DETAIL_BYTES);
    this.outputRejections.push({
      attempt: this.outputRejections.length + 1,
      activity,
      layer,
      reason: sanitizeReason(reason),
      ...(sanitizedDetail === "" ? {} : { detail: sanitizedDetail }),
    });
  }

  snapshot(details = {}) {
    const providerAttempts = this.requests.flatMap((request) => request.attempts.map((attempt) => ({
      activity: attempt.activity,
      logicalCall: request.logicalCall,
      attempt: attempt.attempt,
      messageCount: request.messageCount,
      requestBytes: request.requestBytes,
      toolResultBytes: request.toolResultBytes,
      durationMs: attempt.durationMs ?? Math.max(0, this.now() - attempt.startedAt),
      ...(attempt.status === undefined ? {} : { status: attempt.status }),
      ...(attempt.requestId === undefined ? {} : { requestId: attempt.requestId }),
      ...(attempt.finishReason === undefined ? {} : { finishReason: attempt.finishReason }),
      ...(attempt.usage === undefined ? {} : { usage: attempt.usage }),
      ...(attempt.streamStructuralViolation === undefined
        ? {}
        : { streamStructuralViolation: attempt.streamStructuralViolation }),
      ...(attempt.postFinishShape === undefined ? {} : { postFinishShape: attempt.postFinishShape }),
    })));
    const usage = summarizeUsage(providerAttempts);
    const finishReason = [...providerAttempts].reverse()
      .find((attempt) => attempt.finishReason !== undefined)?.finishReason || "";
    return {
      activity: this.activity,
      durationMs: Math.max(0, this.now() - this.startedAt),
      requestRetryCount: this.requests.reduce(
        (count, request) => count + Math.max(0, request.attempts.length - 1), 0,
      ),
      ...details,
      providerFinishReason: finishReason || null,
      tokenUsage: usage,
      providerAttempts,
      ignoredPostFinishEmptyDeltaChoices: this.ignoredPostFinishEmptyDeltaChoices,
      ignoredRepeatedTerminalChoices: this.ignoredRepeatedTerminalChoices,
      ...(this.providerErrorCode === undefined ? {} : { providerErrorCode: this.providerErrorCode }),
      ...(this.streamStructuralViolation === undefined
        ? {}
        : { streamStructuralViolation: this.streamStructuralViolation }),
      ...(this.postFinishShape === undefined ? {} : { postFinishShape: this.postFinishShape }),
      ...(this.outputRejections.length === 0
        ? {}
        : { outputRejections: this.outputRejections }),
    };
  }
}

function saturatingIncrement(value) {
  return value < Number.MAX_SAFE_INTEGER ? value + 1 : value;
}

// The alphabet is entirely ASCII, so what survives it measures the same in characters as in bytes and
// the budget can be applied by slicing.
function sanitizeReason(reason, maximumBytes = MAX_OUTPUT_REJECTION_REASON_BYTES) {
  return String(reason ?? "")
    .replace(UNSAFE_REASON_CHARACTER, " ")
    .replace(/ +/g, " ")
    .trim()
    .slice(0, maximumBytes)
    .trimEnd();
}

class ResponseBodyIdleError extends Error {
  constructor() {
    super("provider stream made no progress before the idle limit");
    this.name = "ResponseBodyIdleError";
  }
}

class ResponseBodySizeError extends Error {
  constructor() {
    super("provider stream exceeded the byte limit");
    this.name = "ResponseBodySizeError";
  }
}

class ResponseBodyMonitor {
  constructor({ idleTimeoutMs, maximumBytes }) {
    this.idleTimeoutMs = idleTimeoutMs;
    this.maximumBytes = maximumBytes;
    this.bytes = 0;
    this.failure = null;
    this.abort = null;
    this.idleTimer = null;
  }

  beginAttempt(abort) {
    this.finishAttempt();
    this.bytes = 0;
    this.failure = null;
    this.status = undefined;
    this.headers = undefined;
    this.abort = abort;
    this.resetIdleTimer();
  }

  wrap(response) {
    this.status = response.status;
    this.headers = response.headers;
    if (!response.body) {
      this.finishAttempt();
      return response;
    }
    const monitor = this;
    const body = response.body.pipeThrough(new TransformStream({
      transform(chunk, controller) {
        const failure = monitor.observe(chunk);
        if (failure) {
          controller.error(failure);
          return;
        }
        controller.enqueue(chunk);
      },
      flush() {
        monitor.finishAttempt();
      },
    }));
    const wrapped = new Response(body, {
      status: response.status,
      statusText: response.statusText,
      headers: response.headers,
    });
    for (const property of ["url", "redirected", "type"]) {
      Object.defineProperty(wrapped, property, { value: response[property] });
    }
    return wrapped;
  }

  observe(chunk) {
    const length = Number(chunk?.byteLength);
    if (!Number.isSafeInteger(length) || length < 0) {
      return this.fail(new ResponseBodySizeError());
    }
    if (length === 0) return null;
    this.bytes += length;
    if (!Number.isSafeInteger(this.bytes) || this.bytes > this.maximumBytes) {
      return this.fail(new ResponseBodySizeError());
    }
    this.resetIdleTimer();
    return null;
  }

  fail(error) {
    if (this.failure) return this.failure;
    if (Number.isInteger(this.status)) error.status = this.status;
    if (this.headers) error.headers = this.headers;
    this.failure = error;
    const abort = this.abort;
    this.finishAttempt();
    abort?.(error);
    return error;
  }

  finishAttempt() {
    if (this.idleTimer !== null) clearTimeout(this.idleTimer);
    this.idleTimer = null;
    this.abort = null;
  }

  resetIdleTimer() {
    if (this.idleTimer !== null) clearTimeout(this.idleTimer);
    this.idleTimer = setTimeout(
      () => this.fail(new ResponseBodyIdleError()),
      this.idleTimeoutMs,
    );
  }
}

function createResponseBodyMonitor(options) {
  return new ResponseBodyMonitor(options);
}

function responseBodyMonitorFetchOptions(monitor) {
  return { [RESPONSE_BODY_MONITOR]: monitor };
}

function createProviderClient(OpenAIClient, options, metrics, fetch = globalThis.fetch) {
  const instrumentedFetch = async (...args) => {
    const [url, requestOptions = {}] = args;
    const monitor = requestOptions[RESPONSE_BODY_MONITOR];
    const fetchOptions = { ...requestOptions };
    delete fetchOptions[RESPONSE_BODY_MONITOR];
    const attempt = metrics.beginAttempt();
    const controller = new AbortController();
    const parentSignal = fetchOptions.signal;
    const abort = () => {
      monitor?.finishAttempt();
      controller.abort(parentSignal?.reason);
    };
    if (parentSignal?.aborted) {
      abort();
    } else {
      parentSignal?.addEventListener("abort", abort, { once: true });
      attempt.removeAbortListener = () => parentSignal?.removeEventListener("abort", abort);
    }
    monitor?.beginAttempt((reason) => controller.abort(reason));
    try {
      const response = await fetch(url, { ...fetchOptions, signal: controller.signal });
      metrics.observeResponse(attempt, response);
      return monitor ? monitor.wrap(response) : response;
    } catch (error) {
      const monitoredFailure = monitor?.failure;
      monitor?.finishAttempt();
      metrics.finishAttempt(attempt);
      throw monitoredFailure || error;
    }
  };
  return new OpenAIClient({ ...options, maxRetries: 0, fetch: instrumentedFetch });
}

function retryAfterMilliseconds(headers, now = Date.now()) {
  const milliseconds = parseDelay(headers?.get?.("retry-after-ms"), 1);
  if (milliseconds !== undefined) return milliseconds;
  const retryAfter = headers?.get?.("retry-after");
  if (typeof retryAfter !== "string") return undefined;
  const seconds = parseDelay(retryAfter, 1000);
  if (seconds !== undefined) return seconds;
  const date = Date.parse(retryAfter);
  return Number.isFinite(date) && date >= now ? date - now : undefined;
}

function parseDelay(value, multiplier) {
  if (typeof value !== "string" || !/^\d+(?:\.\d+)?$/.test(value)) return undefined;
  const delay = Number(value) * multiplier;
  return Number.isFinite(delay) && delay >= 0 ? delay : undefined;
}

async function delay(milliseconds, signal) {
  let remaining = milliseconds;
  while (remaining > 0 && !signal?.aborted) {
    const chunk = Math.min(remaining, MAX_TIMER_DELAY_MS);
    await new Promise((resolve) => {
      const timer = setTimeout(done, chunk);
      const abort = () => {
        clearTimeout(timer);
        done();
      };
      function done() {
        signal?.removeEventListener("abort", abort);
        resolve();
      }
      signal?.addEventListener("abort", abort, { once: true });
    });
    remaining -= chunk;
  }
}

function normalizeUsage(usage) {
  if (usage === null || typeof usage !== "object" || Array.isArray(usage)) return null;
  const inputTokens = safeTokenCount(usage.prompt_tokens ?? usage.input_tokens);
  const outputTokens = safeTokenCount(usage.completion_tokens ?? usage.output_tokens);
  const totalTokens = safeTokenCount(usage.total_tokens);
  if (inputTokens === undefined && outputTokens === undefined && totalTokens === undefined) return null;
  return {
    ...(inputTokens === undefined ? {} : { inputTokens }),
    ...(outputTokens === undefined ? {} : { outputTokens }),
    ...(totalTokens === undefined ? {} : { totalTokens }),
  };
}

function safeTokenCount(value) {
  return Number.isSafeInteger(value) && value >= 0 ? value : undefined;
}

function summarizeUsage(attempts) {
  const known = attempts.filter((attempt) => attempt.usage !== undefined);
  const summary = {
    complete: attempts.length > 0 && known.length === attempts.length &&
      known.every((attempt) => ["inputTokens", "outputTokens", "totalTokens"]
        .every((field) => attempt.usage[field] !== undefined)),
    knownAttemptCount: known.length,
    unknownAttemptCount: attempts.length - known.length,
  };
  for (const field of ["inputTokens", "outputTokens", "totalTokens"]) {
    const values = known.map((attempt) => attempt.usage[field]).filter((value) => value !== undefined);
    if (values.length !== 0) summary[field] = values.reduce((total, value) => total + value, 0);
  }
  return summary;
}

module.exports = {
  ResponseBodyIdleError, ResponseBodySizeError, RuntimeMetrics, createProviderClient,
  createResponseBodyMonitor, delay, providerErrorCode, responseBodyMonitorFetchOptions,
  retryAfterMilliseconds, sanitizeReason,
};
