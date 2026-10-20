"use strict";

const path = require("node:path");

const { fail } = require("./errors");
const {
  MAX_VALIDATION_DETAIL_BYTES, MAX_VALIDATION_GUIDANCE_BYTES, MAX_VALIDATION_REASON_BYTES,
  MAX_VALIDATOR_METADATA_BYTES,
} = require("./limits");
const { WorkspaceSandbox } = require("./sandbox");

const SAFE_EXPORT = /^[A-Za-z_$][A-Za-z0-9_$]{0,127}$/;
const SAFE_TEXT = /^[A-Za-z0-9][A-Za-z0-9 .,:;()/_-]*$/;

class ValidatorFailure extends Error {
  constructor(reason, category) {
    super(reason);
    this.name = "ValidatorFailure";
    this.reason = reason;
    this.category = category;
  }
}

function parseMetadata(raw) {
  if (raw === "") return {};
  if (Buffer.byteLength(raw, "utf8") > MAX_VALIDATOR_METADATA_BYTES) {
    fail("validator metadata exceeds byte limit", "input");
  }
  let metadata;
  try {
    metadata = JSON.parse(raw);
  } catch {
    fail("validator metadata is not valid JSON", "input");
  }
  if (metadata === null || typeof metadata !== "object" || Array.isArray(metadata)) {
    fail("validator metadata must be an object", "input");
  }
  return metadata;
}

function resolveSelector(workspace, selector, kind) {
  const marker = selector.lastIndexOf("#");
  if (marker <= 0 || marker === selector.length - 1) {
    fail(`invalid ${kind} selector`, "input");
  }
  const modulePath = selector.slice(0, marker);
  const exportName = selector.slice(marker + 1);
  if (!SAFE_EXPORT.test(exportName)) fail(`invalid ${kind} selector`, "input");

  const sandbox = new WorkspaceSandbox(workspace);
  let target;
  try {
    target = sandbox.resolve(modulePath, "file", false);
  } catch (error) {
    if (kind === "normalizer") {
      throw new ValidatorFailure("normalizer module could not be loaded", "normalizer-error");
    }
    throw error;
  }
  let exports;
  try {
    delete require.cache[require.resolve(target.real)];
    exports = require(target.real);
  } catch {
    throw new ValidatorFailure(`${kind} module could not be loaded`, `${kind}-error`);
  }
  const callback = exports?.[exportName];
  if (typeof callback !== "function") {
    throw new ValidatorFailure(`${kind} export is unavailable`, `${kind}-error`);
  }
  return callback;
}

function loadValidator(workspace, selector, metadata) {
  if (selector === "") return null;
  const validate = resolveSelector(workspace, selector, "validator");
  return async (candidate, context) => {
    try {
      const result = await validate(candidate, {
        metadata,
        previousCandidate: context.previousCandidate,
        candidates: context.candidates,
        repairAttempt: context.repairAttempt,
      });
      if (result === null || typeof result !== "object" || Array.isArray(result) ||
          typeof result.ok !== "boolean") {
        throw new ValidatorFailure("validator returned an invalid result", "validator-error");
      }
      if (result.ok) return { ok: true };
      if (!safeReason(result.reason)) {
        throw new ValidatorFailure("validator returned an unsafe rejection reason", "validator-error");
      }
      // The reason is the short record kept when repairs run out. A detail is the full content-free
      // diagnostic for repair and per-attempt diagnostics; guidance is repair-only and may quote
      // trusted evidence, so it never leaves the conversation.
      const rejection = { ok: false, reason: result.reason };
      if (result.detail !== undefined) {
        if (!safeText(result.detail, MAX_VALIDATION_DETAIL_BYTES)) {
          throw new ValidatorFailure("validator returned an unsafe rejection detail", "validator-error");
        }
        rejection.detail = result.detail;
      }
      if (result.guidance !== undefined) {
        if (!safeText(result.guidance, MAX_VALIDATION_GUIDANCE_BYTES)) {
          throw new ValidatorFailure("validator returned unsafe repair guidance", "validator-error");
        }
        rejection.guidance = result.guidance;
      }
      return rejection;
    } catch (error) {
      if (error instanceof ValidatorFailure) throw error;
      throw terminalValidatorFailure(error);
    }
  };
}

function loadNormalizer(workspace, selector) {
  if (selector === "") return null;
  const normalize = resolveSelector(workspace, selector, "normalizer");
  return (candidate) => {
    try {
      return normalize(candidate);
    } catch {
      throw new ValidatorFailure("normalizer execution failed", "normalizer-error");
    }
  };
}

function safeText(value, maximumBytes) {
  return typeof value === "string" &&
    Buffer.byteLength(value, "utf8") <= maximumBytes &&
    SAFE_TEXT.test(value);
}

function safeReason(reason) {
  return safeText(reason, MAX_VALIDATION_REASON_BYTES);
}

function terminalValidatorFailure(error) {
  if (error?.code === "VALIDATOR_TERMINAL") {
    return new ValidatorFailure(
      safeReason(error.message) ? error.message : "validator terminal failure",
      "validator-terminal",
    );
  }
  return new ValidatorFailure("validator execution failed", "validator-error");
}

module.exports = { ValidatorFailure, loadNormalizer, loadValidator, parseMetadata };
