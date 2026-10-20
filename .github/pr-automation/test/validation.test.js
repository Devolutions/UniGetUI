"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { validate, render, agentValidator } = require("../validation");
const { sanitizeTree, addedLines } = require("../evidence");
const { configuration } = require("../stages");
const { report, finding, HEAD, AUTOMATION, env } = require("./fixtures");

test("schema rejects unknown output properties, stale heads and made-up overlaps", () => {
  const { classifier, context } = report();
  assert.equal(validate(classifier, "classifier", context).ok, true);
  assert.equal(validate({ ...classifier, model: "other" }, "classifier", context).ok, false);
  assert.equal(validate({ ...classifier, head: AUTOMATION }, "classifier", context).ok, false);
  assert.equal(validate({ ...classifier, overlap: [22] }, "classifier", context).ok, false);
});
test("specialists require own identity, unique IDs and real added-line locations", () => {
  const { context } = report();
  const candidate = { head: HEAD, reviewer: "skeptical", summary: "A finding.", findings: [finding()], dispositions: [] };
  assert.equal(validate(candidate, "skeptical", context).ok, true);
  for (const patch of [{ path: "src/unchanged.cs" }, { path: "../escape" }, { line: 2 },
    { line: 0 }, { sources: ["general:id"] }]) {
    assert.equal(validate({ ...candidate, findings: [finding(patch)] }, "skeptical", context).ok, false);
  }
  assert.equal(validate({ ...candidate, findings: [finding(), finding()] }, "skeptical", context).ok, false);
  assert.equal(validate(candidate, "dotnet-aot", context).ok, false);
  assert.equal(validate({ ...candidate, findings: [finding({ line: null })] }, "skeptical", context).ok, true);
});
test("general independently disposes of every candidate and enforces unique provenance", () => {
  const { context, review } = report();
  const candidate = { ...review, reviewer: "skeptical", findings: [finding()] };
  assert.equal(validate(review, "general", context, [candidate]).ok, false);
  const accepted = { ...review, findings: [finding({ sources: ["skeptical:a-finding"] })],
    dispositions: [{ candidate: "skeptical:a-finding", decision: "accepted", reason: "Confirmed against code." }] };
  assert.equal(validate(accepted, "general", context, [candidate]).ok, true);
  assert.equal(validate({ ...accepted, findings: [] }, "general", context, [candidate]).ok, false);
  assert.equal(validate({ ...accepted, dispositions: [accepted.dispositions[0], accepted.dispositions[0]] },
    "general", context, [candidate]).ok, false);
  const rejected = { ...accepted, dispositions: [{ ...accepted.dispositions[0], decision: "rejected" }] };
  assert.equal(validate(rejected, "general", context, [candidate]).ok, false);
  assert.equal(validate({ ...rejected, findings: [] }, "general", context, [candidate]).ok, true);
});
test("model text cannot inject HTML, mentions or markdown headings into publication", () => {
  const { review } = report();
  const rendered = render({ ...review, summary: "<script>@everyone</script>\n# forged" });
  assert.equal(rendered.includes("<script>"), false);
  assert.equal(rendered.includes("@everyone"), false);
  assert.ok(rendered.includes("\\# forged"));
  assert.ok(rendered.includes("not approval"));
});
test("semantic callback uses the harness metadata contract and repair cannot drop candidates", () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "unigetui-validator-"));
  try {
    const { context, review } = report();
    const file = path.join(temp, "context.json");
    fs.writeFileSync(file, JSON.stringify(context));
    const candidate = { ...review, reviewer: "skeptical" };
    assert.equal(agentValidator(candidate, { metadata: { stage: "skeptical", context_file: file } }).ok, true);
    assert.equal(agentValidator(candidate, { metadata: { stage: "skeptical", context_file: file },
      previousCandidate: { findings: [finding()] } }).ok, false);
    assert.throws(() => agentValidator(candidate, { metadata: { stage: "skeptical", context_file: file + "-missing" } }),
      (error) => error.code === "VALIDATOR_TERMINAL");
  } finally { fs.rmSync(temp, { recursive: true }); }
});
test("added-line extraction ignores context/deletions and honors hunk positions", () => {
  const diff = "diff --git a/a b/a\n--- a/a\n+++ b/a\n@@ -1,3 +1,4 @@\n context\n-old\n+new\n+extra\n last\n@@ -20 +21 @@\n-old\n+replacement\n";
  assert.deepEqual(addedLines(diff), [2, 3, 21]);
});
test("head sanitization removes nested instructions and automation without following external links", (t) => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "unigetui-tree-"));
  try {
    const root = path.join(temp, "head");
    fs.mkdirSync(path.join(root, "src"), { recursive: true });
    fs.mkdirSync(path.join(root, ".github"));
    fs.writeFileSync(path.join(root, "src", "AGENTS.md"), "untrusted");
    fs.writeFileSync(path.join(root, ".github", "workflow.yml"), "untrusted");
    fs.writeFileSync(path.join(root, "src", "Code.cs"), "text");
    fs.writeFileSync(path.join(temp, "outside.txt"), "private");
    let linked = true;
    try { fs.symlinkSync(temp, path.join(root, "escape"), "junction"); }
    catch (error) {
      if (!["EPERM", "EACCES"].includes(error.code)) throw error;
      linked = false; t.diagnostic("Host denies symlinks; Linux CI covers the external-link case.");
    }
    sanitizeTree(root);
    assert.deepEqual(fs.readdirSync(root), ["src"]);
    assert.deepEqual(fs.readdirSync(path.join(root, "src")), ["Code.cs"]);
    assert.equal(fs.readFileSync(path.join(temp, "outside.txt"), "utf8"), "private");
    if (linked) assert.equal(fs.existsSync(path.join(root, "escape")), false);
    assert.throws(() => sanitizeTree(root, 1), /exceeds/);
  } finally { fs.rmSync(temp, { recursive: true }); }
});
test("runtime profiles cannot receive specialist peers or unrestricted filesystem access", () => {
  const specialist = configuration("skeptical", env);
  assert.deepEqual(specialist.allowed_roots, ["pr-head"]);
  assert.equal(specialist.allowed_files.includes("pr-evidence/aggregate.json"), false);
  assert.equal(configuration("general", env).allowed_files.includes("pr-evidence/aggregate.json"), true);
  assert.ok(specialist.methodology_files.includes(".agents/skills/skeptical-reviewer/SKILL.md"));
  assert.throws(() => configuration("protocol", env), /Unknown/);
});
