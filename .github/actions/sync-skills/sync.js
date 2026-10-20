"use strict";

const fs = require("node:fs");
const path = require("node:path");
const { execFileSync } = require("node:child_process");

const APPROVED = new Set(["skeptical-reviewer", "code-compressor"]);
const MAX_FILES = 256;
const MAX_BYTES = 2 * 1024 * 1024;

function validateManifest(value) {
  if (!value || Object.keys(value).sort().join(",") !== "repository,revision,skills,version" ||
      value.version !== 1 || value.repository !== "Devolutions/devolutions-gateway" ||
      !/^[a-f0-9]{40}$/.test(value.revision) || !Array.isArray(value.skills) ||
      value.skills.length < 1 || value.skills.length > APPROVED.size ||
      new Set(value.skills).size !== value.skills.length ||
      value.skills.some((skill) => !APPROVED.has(skill))) {
    throw new Error("Invalid manifest: only explicitly approved skill directories may be owned");
  }
  return value;
}

function exists(file) {
  try { return fs.lstatSync(file); } catch (error) {
    if (error.code === "ENOENT") return null;
    throw error;
  }
}

function assertPath(file) {
  const absolute = path.resolve(file);
  const root = path.parse(absolute).root;
  let current = root;
  for (const part of absolute.slice(root.length).split(path.sep).filter(Boolean)) {
    current = path.join(current, part);
    const stat = exists(current);
    if (stat?.isSymbolicLink()) throw new Error("Symlink/junction paths are not allowed");
    if (stat && current !== absolute && !stat.isDirectory()) throw new Error("Invalid ancestor");
  }
  return absolute;
}

function inventory(directory, required = true) {
  assertPath(directory);
  const result = new Map();
  let bytes = 0;
  let entries = 0;
  const portableNames = new Set();
  const rootStat = exists(directory);
  if (!rootStat && !required) return result;
  if (!rootStat?.isDirectory()) throw new Error("Expected a skill directory");
  function visit(current, relative, depth) {
    if (depth > 16) throw new Error("Skill tree too deep");
    for (const name of fs.readdirSync(current).sort()) {
      if (++entries > MAX_FILES) throw new Error("Skill tree exceeds limits");
      // Keep names portable, including on NTFS (no ADS, device names, or aliasing).
      if (!/^[A-Za-z0-9_.-]+$/.test(name) || /[. ]$/.test(name) ||
          /^(con|prn|aux|nul|com[0-9]|lpt[0-9])(\.|$)/i.test(name) ||
          name.toLowerCase() === ".git") throw new Error("Unsafe skill entry name");
      const full = path.join(current, name);
      const rel = relative ? `${relative}/${name}` : name;
      const metadataNames = ["LICENSE-MIT", "LICENSE-APACHE", "UPSTREAM.md", ".gitattributes"];
      if (relative === "" && metadataNames.some((entry) =>
        entry.toLowerCase() === name.toLowerCase() && entry !== name)) {
        throw new Error("Case-aliased metadata path");
      }
      if (portableNames.has(rel.toLowerCase())) throw new Error("Case-colliding skill entries");
      portableNames.add(rel.toLowerCase());
      const stat = fs.lstatSync(full);
      if (stat.isSymbolicLink()) throw new Error("Symlinks/junctions are not allowed");
      if (stat.isDirectory()) {
        if (relative === "" && metadataNames.some((entry) => entry.toLowerCase() === name.toLowerCase())) {
          throw new Error("Reserved metadata path must not be a directory");
        }
        visit(full, rel, depth + 1);
      } else {
        if (!stat.isFile() || stat.nlink !== 1) throw new Error("Only unlinked regular files allowed");
        bytes += stat.size;
        if (bytes > MAX_BYTES || result.size >= MAX_FILES) throw new Error("Skill tree exceeds limits");
        result.set(rel, fs.readFileSync(full));
      }
    }
  }
  visit(directory, "", 0);
  return result;
}

function provenance(manifest, skill) {
  return `# Shared skill provenance\n\nMirrored from \`${manifest.repository}\` at commit\n` +
    `\`${manifest.revision}\`, directory \`.agents/skills/${skill}\`.\n\n` +
    `Source: https://github.com/${manifest.repository}/tree/${manifest.revision}/.agents/skills/${skill}\n\n` +
    "Upstream is dual-licensed MIT OR Apache-2.0; both license texts are retained.\n" +
    "SKILL.md is mirrored verbatim. Only this explicitly approved directory is sync-owned.\n";
}

function syncSkills({ sourceDirectory, targetDirectory, manifest, mode = "preview" }) {
  validateManifest(manifest);
  if (!["preview", "apply"].includes(mode)) throw new Error("Mode must be preview or apply");
  const source = assertPath(sourceDirectory);
  const target = assertPath(targetDirectory);
  if (!fs.statSync(source).isDirectory() || !fs.statSync(target).isDirectory()) {
    throw new Error("Source and target roots must exist");
  }
  const plans = manifest.skills.map((skill) => {
    const upstream = path.join(source, ".agents", "skills", skill);
    const destination = path.join(target, ".agents", "skills", skill);
    const from = path.resolve(upstream).toLowerCase();
    const to = path.resolve(destination).toLowerCase();
    if (from === to) throw new Error("Source equals destination");
    if (from.startsWith(`${to}${path.sep}`) || to.startsWith(`${from}${path.sep}`)) {
      throw new Error("Source and destination skill trees must not overlap");
    }
    const desired = inventory(upstream);
    if (!desired.has("SKILL.md")) throw new Error("Missing upstream SKILL.md");
    for (const license of ["LICENSE-MIT", "LICENSE-APACHE"]) {
      const file = assertPath(path.join(source, license));
      const stat = fs.lstatSync(file);
      if (!stat.isFile() || stat.nlink !== 1 || stat.size > MAX_BYTES) {
        throw new Error("Invalid upstream license");
      }
      desired.set(license, fs.readFileSync(file));
    }
    desired.set("UPSTREAM.md", Buffer.from(provenance(manifest, skill)));
    desired.set(".gitattributes", Buffer.from("* -text\n"));
    const existing = inventory(destination, false);
    const changes = [...new Set([...desired.keys(), ...existing.keys()])].sort()
      .filter((name) => !desired.get(name)?.equals(existing.get(name) ?? Buffer.alloc(0)) ||
        !desired.has(name) || !existing.has(name))
      .map((name) => ({
        path: `.agents/skills/${skill}/${name}`,
        change: !desired.has(name) ? "delete" : !existing.has(name) ? "add" : "modify",
      }));
    return { destination, desired, changes };
  });
  // Validate every source and destination before touching any explicitly owned directory.
  if (mode === "apply") {
    for (const { destination, desired, changes } of plans) {
      if (!changes.length) continue;
      assertPath(destination);
      fs.rmSync(destination, { recursive: true, force: true });
      fs.mkdirSync(destination, { recursive: true });
      for (const [relative, content] of desired) {
        const file = path.join(destination, ...relative.split("/"));
        fs.mkdirSync(path.dirname(file), { recursive: true });
        fs.writeFileSync(file, content, { flag: "wx" });
      }
    }
  }
  return { mode, repository: manifest.repository, revision: manifest.revision,
    changes: plans.flatMap((plan) => plan.changes) };
}

if (require.main === module) {
  try {
    const [sourceDirectory, targetDirectory, manifestPath, mode = "preview"] = process.argv.slice(2);
    if (!sourceDirectory || !targetDirectory || !manifestPath || process.argv.length > 6) {
      throw new Error("Usage: node sync.js SOURCE_DIRECTORY TARGET_DIRECTORY MANIFEST [preview|apply]");
    }
    const file = assertPath(manifestPath);
    const stat = fs.lstatSync(file);
    if (!stat.isFile() || stat.size > 16384) throw new Error("Invalid manifest file");
    const manifest = JSON.parse(fs.readFileSync(file, "utf8"));
    validateManifest(manifest);
    const revision = execFileSync("git", ["-C", assertPath(sourceDirectory),
      "rev-parse", "--verify", "HEAD^{commit}"], { encoding: "utf8", timeout: 10000 }).trim();
    if (revision !== manifest.revision) throw new Error("Upstream checkout does not match pinned revision");
    console.log(JSON.stringify(syncSkills({ sourceDirectory, targetDirectory, manifest, mode }), null, 2));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}

module.exports = { syncSkills, validateManifest };
