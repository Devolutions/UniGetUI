"use strict";

const fs = require("node:fs");
const path = require("node:path");
const { execFileSync } = require("node:child_process");
const { policy, SHA, safePath } = require("./policy");
const { sameIdentity, currentPr } = require("./github");

const denied = new Set([".git", ".claude", ".gemini", ".cursor", ".agents", "node_modules",
  "AGENTS.md", "CLAUDE.md", "CLAUDE.local.md", "GEMINI.md", ".cursorrules", "copilot-instructions.md", "instructions"]);

function addedLines(diff) {
  const lines = [];
  let current = 0;
  for (const line of diff.split("\n")) {
    const match = /^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@/.exec(line);
    if (match) current = Number(match[1]);
    else if (current > 0 && line.startsWith("+")) { lines.push(current); current++; }
    else if (current > 0 && line.startsWith(" ")) current++;
    else if (line.startsWith("diff --git ")) current = 0;
  }
  return lines;
}

function sanitizeTree(root, maximum = policy.maxTreeBytes) {
  let bytes = 0;
  let count = 0;
  const binaryContextOmissions = [];
  function walk(dir) {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const target = path.join(dir, entry.name);
      const stat = fs.lstatSync(target);
      if (stat.isSymbolicLink()) { fs.unlinkSync(target); continue; }
      if (denied.has(entry.name) || entry.name === ".github") {
        // Contributor automation is retained in the diff, never loaded as methodology.
        fs.rmSync(target, { recursive: stat.isDirectory() });
        continue;
      }
      if (stat.isDirectory()) walk(target);
      else if (stat.isFile()) {
        const header = Buffer.alloc(Math.min(8192, stat.size));
        const descriptor = fs.openSync(target, "r");
        try { fs.readSync(descriptor, header, 0, header.length, 0); }
        finally { fs.closeSync(descriptor); }
        if (header.includes(0)) {
          binaryContextOmissions.push(path.relative(root, target).split(path.sep).join("/"));
          fs.unlinkSync(target);
          continue;
        }
        bytes += stat.size; count++;
        if (bytes > maximum || count > 20000) throw new Error("Head context exceeds evidence limits");
      } else throw new Error("Unsupported head-tree file type");
    }
  }
  walk(root);
  return { bytes, count, binaryContextOmissions };
}

function collectDiff(git, snapshot) {
  const mergeBase = git("merge-base", snapshot.base, snapshot.head).trim();
  if (!SHA.test(mergeBase)) throw new Error("Merge base unavailable");
  let total = 0;
  const diffs = [];
  const files = snapshot.files.map((file) => {
    const paths = [file.filename, file.previous_filename].filter(Boolean);
    if (!paths.every(safePath)) throw new Error("Unsafe evidence path");
    const diff = git("diff", "--no-ext-diff", "--no-textconv", "--no-color", "--find-renames", "--unified=3",
      mergeBase, snapshot.head, "--", ...paths);
    total += Buffer.byteLength(diff);
    if (total > policy.maxDiffBytes) throw new Error("Diff exceeds the complete-evidence limit");
    diffs.push(diff);
    return { ...file, addedLines: addedLines(diff) };
  });
  const diff = diffs.join("\n");
  if (Buffer.byteLength(diff) > policy.maxDiffBytes) throw new Error("Diff exceeds the complete-evidence limit");
  return { mergeBase, files, diff };
}

async function prepare(github, repo, snapshot, automationSha, root = process.cwd()) {
  if (!SHA.test(snapshot.head) || !SHA.test(snapshot.base) || !SHA.test(automationSha)) throw new Error("Invalid evidence identity");
  const pr = await currentPr(github, repo, snapshot.number);
  if (!sameIdentity(pr, snapshot)) throw new Error("Pull request changed before evidence collection");
  const headDir = path.join(root, "pr-head");
  const evidenceDir = path.join(root, "pr-evidence");
  fs.mkdirSync(headDir);
  fs.mkdirSync(evidenceDir);
  const environment = {
    PATH: process.env.PATH, HOME: process.env.HOME, SystemRoot: process.env.SystemRoot,
    TEMP: process.env.TEMP, TMP: process.env.TMP,
    GIT_CONFIG_NOSYSTEM: "1", GIT_CONFIG_GLOBAL: process.platform === "win32" ? "NUL" : "/dev/null",
    GIT_TERMINAL_PROMPT: "0", GIT_LFS_SKIP_SMUDGE: "1",
  };
  const git = (...args) => {
    try {
      const output = execFileSync("git", ["-c", "core.hooksPath=/dev/null", "-c", "protocol.file.allow=never", ...args], {
        cwd: headDir, env: environment, maxBuffer: policy.maxDiffBytes + 1, timeout: 120000,
        stdio: ["ignore", "pipe", "pipe"],
      });
      return new TextDecoder("utf-8", { fatal: true }).decode(output);
    } catch { throw new Error("Git evidence operation failed or exceeded limits"); }
  };
  git("init", "--template=");
  git("remote", "add", "origin", `https://github.com/${repo.owner}/${repo.repo}.git`);
  git("fetch", "--no-tags", "origin", snapshot.head, snapshot.base);
  // Unset contributor-controlled diff attributes while retaining binary detection.
  fs.mkdirSync(path.join(headDir, ".git", "info"), { recursive: true });
  fs.writeFileSync(path.join(headDir, ".git", "info", "attributes"), "* !diff\n");
  const { mergeBase, files, diff } = collectDiff(git, snapshot);
  // No hooks, submodules, package scripts, or actions from this checkout are executed.
  git("checkout", "--detach", snapshot.head);
  const tree = sanitizeTree(headDir);
  const open = await github.paginate(github.rest.pulls.list, { ...repo, state: "open", per_page: 100 });
  if (open.length > 500) throw new Error("Overlap context exceeds limits");
  const overlap = open.filter((item) => item.number !== pr.number).map((item) =>
    ({ number: item.number, title: item.title.slice(0, 500) }));
  const comments = await github.paginate(github.rest.issues.listComments, { ...repo, issue_number: pr.number, per_page: 100 });
  const reviewComments = await github.paginate(github.rest.pulls.listReviewComments, { ...repo, pull_number: pr.number, per_page: 100 });
  const reviews = await github.paginate(github.rest.pulls.listReviews, { ...repo, pull_number: pr.number, per_page: 100 });
  if (comments.length + reviewComments.length + reviews.length > 1000) throw new Error("Discussion exceeds evidence limits");
  const discussion = [...comments, ...reviewComments, ...reviews].map((entry) => ({
    author: entry.user?.login || null, body: entry.body || "", state: entry.state || null,
    path: entry.path || null, line: entry.line || null, commit: entry.commit_id || null,
  }));
  const context = {
    number: pr.number, head: snapshot.head, base: snapshot.base, automationSha, mergeBase,
    title: pr.title, body: pr.body || "", files, overlap, discussion, tree,
  };
  if (Buffer.byteLength(JSON.stringify(context)) > 4 * 1024 * 1024) throw new Error("PR context exceeds evidence limits");
  if (!sameIdentity(await currentPr(github, repo, pr.number), snapshot)) throw new Error("Pull request changed during evidence collection");
  fs.writeFileSync(path.join(evidenceDir, "context.json"), JSON.stringify(context));
  fs.writeFileSync(path.join(evidenceDir, "pull-request.diff"), diff);
  return context;
}

module.exports = { addedLines, sanitizeTree, collectDiff, prepare };
