"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { execFileSync } = require("node:child_process");
const { collectDiff, sanitizeTree } = require("../evidence");
const { file } = require("./fixtures");

test("real Git evidence ignores contributor diff attributes and binds lines to an immutable merge base", () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "unigetui-git-"));
  try {
    const git = (...args) => execFileSync("git", ["-c", "core.hooksPath=/dev/null",
      "-c", "user.name=Automation Tests", "-c", "user.email=tests@example.invalid",
      "-c", "commit.gpgsign=false", ...args], { cwd: temp, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
    git("init", "--template=");
    fs.writeFileSync(path.join(temp, ".gitattributes"), "*.cs -diff\n");
    fs.writeFileSync(path.join(temp, "Code.cs"), "old\n");
    git("add", "."); git("commit", "-qm", "base");
    const base = git("rev-parse", "HEAD").trim();
    fs.writeFileSync(path.join(temp, "Code.cs"), "new\nanother\n");
    git("add", "."); git("commit", "-qm", "head");
    const head = git("rev-parse", "HEAD").trim();
    fs.mkdirSync(path.join(temp, ".git", "info"), { recursive: true });
    fs.writeFileSync(path.join(temp, ".git", "info", "attributes"), "* !diff\n");
    const result = collectDiff(git, { head, base, files: [file("Code.cs", 2, 1)] });
    assert.equal(result.mergeBase, base);
    assert.match(result.diff, /\+another/);
    assert.doesNotMatch(result.diff, /Binary files/);
    assert.deepEqual(result.files[0].addedLines, [1, 2]);
    fs.writeFileSync(path.join(temp, "Code.cs"), "uncommitted and untrusted\n");
    assert.equal(collectDiff(git, { head, base, files: [file("Code.cs")] }).diff, result.diff);
  } finally { fs.rmSync(temp, { recursive: true }); }
});

test("oversized complete evidence and unsafe manifests fail rather than truncate", () => {
  const sha = "a".repeat(40);
  const snapshot = { head: sha, base: sha, files: [file("a")] };
  assert.throws(() => collectDiff((operation) => operation === "merge-base" ? sha : "a".repeat(4194305), snapshot),
    /complete-evidence/);
  assert.throws(() => collectDiff(() => sha, { ...snapshot, files: [file("../a")] }), /Unsafe/);
  assert.throws(() => collectDiff(() => "not-a-commit", snapshot), /Merge base/);
});

test("binary context exclusions are explicit and do not consume the text-tree budget", () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "unigetui-binary-"));
  try {
    fs.writeFileSync(path.join(temp, "asset.png"), Buffer.from([0, 1, 2]));
    fs.writeFileSync(path.join(temp, "Code.cs"), "text");
    const result = sanitizeTree(temp, 4);
    assert.deepEqual(result, { bytes: 4, count: 1, binaryContextOmissions: ["asset.png"] });
    assert.deepEqual(fs.readdirSync(temp), ["Code.cs"]);
  } finally { fs.rmSync(temp, { recursive: true }); }
});
