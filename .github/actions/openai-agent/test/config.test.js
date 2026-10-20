"use strict";

const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const assert = require("node:assert/strict");

const { loadConfiguration, validateBaseUrl } = require("../src/config");
const { MAX_METHODOLOGY_TOTAL_BYTES, MAX_PROMPT_CONTEXT_BYTES } = require("../src/limits");
const { scratchWorkspace, write } = require("./helpers");

function configurationFixture(changes = {}) {
  const workspace = scratchWorkspace();
  fs.mkdirSync(path.join(workspace.directory, "root"));
  write(workspace.directory, "prompt.md", "configured prompt");
  write(workspace.directory, "method.md", "configured method");
  write(workspace.directory, "schema.json", JSON.stringify({
    type: "object",
    required: ["ok"],
    properties: { ok: { type: "boolean" } },
  }));
  const config = {
    id: "generic-test",
    model: "model-1",
    prompt_file: "prompt.md",
    schema_file: "schema.json",
    methodology_files: ["method.md"],
    allowed_roots: ["root"],
    allowed_files: [],
    max_turns: 10,
    max_tool_calls: 20,
    ...changes,
  };
  write(workspace.directory, "config.json", JSON.stringify(config));
  return workspace;
}

test("configuration loads workflow artifacts and constructs capabilities", () => {
  const workspace = configurationFixture();
  try {
    const loaded = loadConfiguration(workspace.directory, "config.json");
    assert.equal(loaded.config.id, "generic-test");
    assert.equal(loaded.prompt, "configured prompt");
    assert.deepEqual(loaded.methodologies, ["configured method"]);
    assert.equal(loaded.schema.type, "object");
    assert.doesNotThrow(() => loaded.sandbox.listFiles({ path: "root" }));
  } finally {
    workspace.cleanup();
  }
});

test("a trusted prompt context follows the configured prompt within its own bound", () => {
  const workspace = configurationFixture();
  try {
    write(workspace.directory, "prompt.md", "configured prompt\n\n");
    assert.equal(loadConfiguration(workspace.directory, "config.json", "").prompt, "configured prompt\n\n");
    assert.equal(
      loadConfiguration(workspace.directory, "config.json", "candidate index").prompt,
      "configured prompt\n\ncandidate index",
    );
    assert.doesNotThrow(() => loadConfiguration(
      workspace.directory, "config.json", "x".repeat(MAX_PROMPT_CONTEXT_BYTES)));
    assert.throws(
      () => loadConfiguration(workspace.directory, "config.json", "x".repeat(MAX_PROMPT_CONTEXT_BYTES + 1)),
      /prompt context exceeds byte limit/,
    );
  } finally {
    workspace.cleanup();
  }
});

test("configuration rejects unknown fields, unsafe models, and empty capabilities", () => {
  for (const changes of [
    { unexpected: true },
    { model: "unsafe model\n" },
    { allowed_roots: [], allowed_files: [] },
    { max_output_bytes: 32 * 1024 },
    { max_turns: 51 },
    { max_tool_calls: 201 },
    { stream_idle_timeout_ms: 300_001 },
    { stage_timeout_ms: 7_200_001 },
    { max_request_retries: 11 },
    { max_output_repair_attempts: 6 },
    { output_format: "unsupported" },
  ]) {
    const workspace = configurationFixture(changes);
    try {
      assert.throws(() => loadConfiguration(workspace.directory, "config.json"));
    } finally {
      workspace.cleanup();
    }
  }
});

test("configuration accepts bounded recovery controls and gates strict schema output", () => {
  const workspace = configurationFixture({
    stream_idle_timeout_ms: 90_000,
    stage_timeout_ms: 900_000,
    max_request_retries: 4,
    max_output_repair_attempts: 2,
    output_format: "json_schema",
  });
  try {
    const loaded = loadConfiguration(workspace.directory, "config.json");
    assert.equal(loaded.config.stream_idle_timeout_ms, 90_000);
    assert.equal(loaded.config.stage_timeout_ms, 900_000);
    assert.equal(loaded.config.max_request_retries, 4);
    assert.equal(loaded.config.max_output_repair_attempts, 2);
    assert.equal(loaded.config.output_format, "json_schema");
  } finally {
    workspace.cleanup();
  }

  const invalid = configurationFixture({
    output_format: "json_schema",
    schema_file: "array-schema.json",
  });
  try {
    write(invalid.directory, "array-schema.json", JSON.stringify({
      type: "array",
      items: { type: "string" },
    }));
    assert.throws(() => loadConfiguration(invalid.directory, "config.json"));
  } finally {
    invalid.cleanup();
  }
});

test("configuration bounds aggregate methodology bytes", () => {
  const workspace = configurationFixture({ methodology_files: ["one.md", "two.md", "three.md", "four.md", "five.md"] });
  try {
    for (const name of ["one.md", "two.md", "three.md", "four.md", "five.md"]) {
      write(workspace.directory, name, "x".repeat(Math.floor(MAX_METHODOLOGY_TOTAL_BYTES / 4)));
    }
    assert.throws(
      () => loadConfiguration(workspace.directory, "config.json"),
      /methodology files exceed byte limit/,
    );
  } finally {
    workspace.cleanup();
  }
});

test("base URL validation permits HTTPS and local HTTP but rejects credential-bearing or redirect-like URLs", () => {
  assert.equal(validateBaseUrl("https://provider.example/v1/"), "https://provider.example/v1");
  assert.equal(validateBaseUrl("http://localhost:8080/v1"), "http://localhost:8080/v1");
  for (const invalid of [
    "http://provider.example/v1",
    "https://user:secret@provider.example/v1",
    "https://provider.example/v1?key=secret",
    "https://provider.example/v1#fragment",
    "file:///etc/passwd",
    "not a URL",
  ]) {
    assert.throws(() => validateBaseUrl(invalid), undefined, invalid);
  }
});
