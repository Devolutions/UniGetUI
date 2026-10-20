"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { configuration } = require("../stages");
const { ROOT } = require("../policy");
const profiles = require("../agents/profiles.json");
const { loadConfiguration } = require("../../actions/openai-agent/src/config");
const { loadValidator } = require("../../actions/openai-agent/src/validator");
const { env, report } = require("./fixtures");

test("every generated reviewer configuration loads through the actual bounded runtime", () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "unigetui-runtime-contract-"));
  try {
    fs.mkdirSync(path.join(temp, "pr-evidence"));
    fs.mkdirSync(path.join(temp, "pr-head"));
    fs.writeFileSync(path.join(temp, "pr-evidence", "context.json"), JSON.stringify(report().context));
    fs.writeFileSync(path.join(temp, "pr-evidence", "pull-request.diff"), "A complete fixture diff.");
    fs.writeFileSync(path.join(temp, "pr-evidence", "aggregate.json"), "[]");
    for (const stage of Object.keys(profiles)) {
      const config = configuration(stage, env);
      const schema = JSON.parse(fs.readFileSync(path.join(ROOT, config.schema_file), "utf8"));
      fs.mkdirSync(path.dirname(path.join(temp, config.schema_file)), { recursive: true });
      fs.writeFileSync(path.join(temp, config.schema_file), JSON.stringify(schema));
      fs.writeFileSync(path.join(temp, "pr-evidence", "prompt.md"), "Trusted test prompt.");
      for (const file of config.methodology_files) {
        fs.mkdirSync(path.dirname(path.join(temp, file)), { recursive: true });
        fs.copyFileSync(path.join(ROOT, file), path.join(temp, file));
      }
      fs.writeFileSync(path.join(temp, "config.json"), JSON.stringify(config));
      const loaded = loadConfiguration(temp, "config.json");
      assert.equal(loaded.config.id, stage);
      assert.equal(loaded.schema.type, "object");
      assert.ok(loaded.methodologies.length > 0);
    }
  } finally { fs.rmSync(temp, { recursive: true }); }
});

test("actual runtime validator loader accepts the UniGetUI callback and repairs wrong identity", async () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "unigetui-callback-"));
  try {
    const { context, classifier } = report();
    const contextFile = path.join(temp, "context.json");
    fs.writeFileSync(contextFile, JSON.stringify(context));
    const validator = loadValidator(ROOT, ".github/pr-automation/validation.js#agentValidator",
      { stage: "classifier", context_file: contextFile });
    assert.deepEqual(await validator(classifier, { previousCandidate: null }), { ok: true });
    assert.equal((await validator({ ...classifier, head: "e".repeat(40) }, { previousCandidate: null })).ok, false);
  } finally { fs.rmSync(temp, { recursive: true }); }
});
