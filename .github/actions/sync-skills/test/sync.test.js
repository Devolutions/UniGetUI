"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { execFileSync, spawnSync } = require("node:child_process");
const { test } = require("node:test");
const { syncSkills, validateManifest } = require("../sync");

const revision = "9ce7daa94442dd0c9677e0b8881a98c92b075f85";
const skills = ["skeptical-reviewer", "code-compressor"];
const translations = ["translation-diff-export", "translation-diff-import", "translation-diff-translate",
  "translation-review", "translation-source-sync", "translation-status"];
const manifest = { version: 1, repository: "Devolutions/devolutions-gateway", revision, skills };

function write(root, relative, value) {
  const file = path.join(root, ...relative.split("/"));
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, value);
}

function fixture(t) {
  const root = fs.mkdtempSync(path.join(__dirname, "..", ".test-workspaces-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const source = path.join(root, "source");
  const target = path.join(root, "target");
  for (const skill of skills) {
    write(source, `.agents/skills/${skill}/SKILL.md`, `# ${skill}\n`);
    write(target, `.agents/skills/${skill}/SKILL.md`, "# old\n");
    write(target, `.agents/skills/${skill}/stale/subdir/old.md`, "obsolete");
  }
  for (const skill of translations) {
    write(target, `.agents/skills/${skill}/SKILL.md`, `local ${skill}`);
    write(target, `.agents/skills/${skill}/scripts/tool.py`, "local script\n");
  }
  write(target, ".agents/other/config", "unrelated");
  write(target, ".agents/skills/unapproved/SKILL.md", "unrelated skill");
  write(source, "LICENSE-MIT", "MIT fixture");
  write(source, "LICENSE-APACHE", "Apache fixture");
  return { root, source, target,
    run: (extra = {}) => syncSkills({ sourceDirectory: source, targetDirectory: target, manifest, ...extra }) };
}

function snapshot(root) {
  const files = {};
  function visit(dir) {
    for (const name of fs.readdirSync(dir).sort()) {
      const full = path.join(dir, name);
      const stat = fs.lstatSync(full);
      const relative = path.relative(root, full).split(path.sep).join("/");
      if (stat.isSymbolicLink()) files[relative] = `link:${fs.readlinkSync(full)}`;
      else if (stat.isDirectory()) { files[relative] = "directory"; visit(full); }
      else files[relative] = fs.readFileSync(full).toString("hex");
    }
  }
  visit(root);
  return files;
}

test("preview defaults to zero writes and reports exact owned changes", (t) => {
  const f = fixture(t);
  const before = snapshot(f.root);
  const report = f.run();
  assert.equal(report.mode, "preview");
  assert.equal(report.revision, revision);
  assert.equal(report.changes.length, 12);
  assert.deepEqual(report.changes.filter((item) => item.change === "delete").map((item) => item.path),
    skills.map((skill) => `.agents/skills/${skill}/stale/subdir/old.md`));
  assert.ok(report.changes.every((item) => skills.some((skill) => item.path.startsWith(`.agents/skills/${skill}/`))));
  assert.deepEqual(snapshot(f.root), before);
});

test("apply replaces only explicit skills and preserves all six translation trees", (t) => {
  const f = fixture(t);
  const before = snapshot(f.target);
  f.run({ mode: "apply" });
  const after = snapshot(f.target);
  for (const [name, content] of Object.entries(before)) {
    if (!skills.some((skill) => name === `.agents/skills/${skill}` || name.startsWith(`.agents/skills/${skill}/`))) {
      assert.equal(after[name], content, name);
    }
  }
  for (const skill of skills) {
    const directory = path.join(f.target, ".agents", "skills", skill);
    assert.equal(fs.readFileSync(path.join(directory, "SKILL.md"), "utf8"), `# ${skill}\n`);
    assert.equal(fs.existsSync(path.join(directory, "stale")), false);
    assert.equal(fs.readFileSync(path.join(directory, "LICENSE-MIT"), "utf8"), "MIT fixture");
    assert.equal(fs.readFileSync(path.join(directory, ".gitattributes"), "utf8"), "* -text\n");
    assert.match(fs.readFileSync(path.join(directory, "UPSTREAM.md"), "utf8"), new RegExp(revision));
  }
  assert.deepEqual(f.run().changes, []);
  const stable = snapshot(f.target);
  f.run({ mode: "apply" });
  assert.deepEqual(snapshot(f.target), stable);
});

test("subset ownership never removes an omitted approved directory", (t) => {
  const f = fixture(t);
  const other = path.join(f.target, ".agents", "skills", "code-compressor");
  const before = snapshot(other);
  f.run({ mode: "apply", manifest: { ...manifest, skills: ["skeptical-reviewer"] } });
  assert.deepEqual(snapshot(other), before);
});

test("manifest rejects traversal absolute paths unapproved trees and broad ownership", () => {
  for (const skill of ["../translation-review", "..", ".", ".agents", "translation-review",
    "/absolute", "C:\\absolute", "skeptical-reviewer/../code-compressor", "skeptical-reviewer\\nested"]) {
    assert.throws(() => validateManifest({ ...manifest, skills: [skill] }), /Invalid manifest/);
  }
  for (const update of [{ skills: [] }, { skills: [skills[0], skills[0]] },
    { revision: "main" }, { repository: "attacker/repo" }, { version: 2 }, { target: ".agents" }]) {
    assert.throws(() => validateManifest({ ...manifest, ...update }), /Invalid manifest/);
  }
  assert.equal(validateManifest(manifest), manifest);
});

test("invalid modes fail without changing files", (t) => {
  const f = fixture(t);
  const before = snapshot(f.root);
  assert.throws(() => f.run({ mode: "publish" }), /Mode/);
  assert.deepEqual(snapshot(f.root), before);
});

test("missing later source fails preflight before earlier skill replacement", (t) => {
  const f = fixture(t);
  fs.unlinkSync(path.join(f.source, ".agents", "skills", "code-compressor", "SKILL.md"));
  const before = snapshot(f.target);
  assert.throws(() => f.run({ mode: "apply" }), /Missing upstream SKILL/);
  assert.deepEqual(snapshot(f.target), before);
});

test("missing license fails without writes", (t) => {
  const f = fixture(t);
  fs.unlinkSync(path.join(f.source, "LICENSE-MIT"));
  const before = snapshot(f.target);
  assert.throws(() => f.run({ mode: "apply" }), /ENOENT/);
  assert.deepEqual(snapshot(f.target), before);
});

test("source destination identity is rejected", (t) => {
  const f = fixture(t);
  assert.throws(() => f.run({ targetDirectory: f.source, mode: "apply" }), /Source equals destination/);
});

for (const location of ["source", "target", "ancestor", "root"]) {
  test(`rejects ${location} symlinks and junctions without touching outside trees`, (t) => {
    const f = fixture(t);
    const outside = path.join(f.root, "outside");
    write(outside, "sentinel", "do not touch");
    const linked = location === "ancestor" ? path.join(f.target, ".agents")
      : location === "root" ? path.join(f.root, "alias")
      : path.join(f[location], ".agents", "skills", skills[1], "linked");
    if (location === "ancestor") fs.rmSync(linked, { recursive: true });
    fs.symlinkSync(outside, linked, process.platform === "win32" ? "junction" : "dir");
    const before = snapshot(f.root);
    assert.throws(() => f.run({ mode: "apply", ...(location === "root" ? { targetDirectory: linked } : {}) }), /[Ss]ymlink/);
    assert.deepEqual(snapshot(f.root), before);
  });
}

test("hard-linked files are rejected", (t) => {
  const f = fixture(t);
  const outside = path.join(f.root, "outside.md");
  fs.writeFileSync(outside, "sentinel");
  fs.linkSync(outside, path.join(f.source, ".agents", "skills", skills[0], "hardlink.md"));
  const before = snapshot(f.target);
  assert.throws(() => f.run({ mode: "apply" }), /regular files/);
  assert.deepEqual(snapshot(f.target), before);
  assert.equal(fs.readFileSync(outside, "utf8"), "sentinel");
});

test("unsafe portable entry names and reserved metadata directories are rejected", (t) => {
  const f = fixture(t);
  write(f.source, `.agents/skills/${skills[0]}/UPSTREAM.md/nested`, "bad");
  const before = snapshot(f.target);
  assert.throws(() => f.run({ mode: "apply" }), /Reserved metadata/);
  assert.deepEqual(snapshot(f.target), before);
  fs.rmSync(path.join(f.source, ".agents", "skills", skills[0], "UPSTREAM.md"), { recursive: true });
  write(f.source, `.agents/skills/${skills[0]}/.git/config`, "bad");
  assert.throws(() => f.run(), /Unsafe skill entry/);
});

test("oversized sources are rejected before writes", (t) => {
  const f = fixture(t);
  write(f.source, `.agents/skills/${skills[1]}/large.md`, Buffer.alloc(2 * 1024 * 1024 + 1));
  const before = snapshot(f.target);
  assert.throws(() => f.run({ mode: "apply" }), /exceeds limits/);
  assert.deepEqual(snapshot(f.target), before);
});

test("manifest owns exactly the approved shared skills, never translations", () => {
  const local = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "..", "..", "skills-sync.yml"), "utf8"));
  assert.deepEqual(validateManifest(local).skills, skills);
  assert.ok(local.skills.every((skill) => !translations.includes(skill)));
});

test("CLI verifies the pinned commit and defaults to preview without writes", (t) => {
  const f = fixture(t);
  const hooks = path.join(f.root, "empty-hooks");
  fs.mkdirSync(hooks);
  const git = (...args) => execFileSync("git", ["-C", f.source,
    "-c", `core.hooksPath=${hooks}`, "-c", "commit.gpgsign=false",
    "-c", "user.name=Offline Test", "-c", "user.email=offline@example.invalid", ...args],
  { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] }).trim();
  git("init", "--quiet");
  git("add", ".");
  git("commit", "--quiet", "-m", "offline source fixture");
  const pinned = git("rev-parse", "HEAD");
  const file = path.join(f.root, "manifest.json");
  fs.writeFileSync(file, JSON.stringify({ ...manifest, revision: pinned }));
  const args = [path.join(__dirname, "..", "sync.js"), f.source, f.target, file];
  const before = snapshot(f.target);
  const preview = spawnSync(process.execPath, args, { encoding: "utf8" });
  assert.equal(preview.status, 0, preview.stderr);
  assert.equal(JSON.parse(preview.stdout).mode, "preview");
  assert.equal(JSON.parse(preview.stdout).revision, pinned);
  assert.deepEqual(snapshot(f.target), before);
  fs.writeFileSync(file, JSON.stringify(manifest));
  const mismatch = spawnSync(process.execPath, [...args, "apply"], { encoding: "utf8" });
  assert.equal(mismatch.status, 1);
  assert.match(mismatch.stderr, /does not match pinned revision/);
  assert.deepEqual(snapshot(f.target), before);
});

test("new approved destinations are created without replacing the skills root", (t) => {
  const f = fixture(t);
  for (const skill of skills) {
    fs.rmSync(path.join(f.target, ".agents", "skills", skill), { recursive: true });
  }
  const localSkill = path.join(f.target, ".agents", "skills", translations[0], "SKILL.md");
  const before = fs.readFileSync(localSkill);
  f.run({ mode: "apply" });
  for (const skill of skills) {
    assert.equal(fs.readFileSync(path.join(f.target, ".agents", "skills", skill, "SKILL.md"), "utf8"), `# ${skill}\n`);
  }
  assert.deepEqual(fs.readFileSync(localSkill), before);
});
