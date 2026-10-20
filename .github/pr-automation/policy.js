"use strict";

const fs = require("node:fs");
const path = require("node:path");
const YAML = require("yaml");
const policy = require("./policy.json");
const ROOT = path.resolve(__dirname, "..", "..");

const SHA = /^[a-f0-9]{40}$/;
const REVIEWERS = policy.reviewers;

function modes(env = {}) {
  const mode = env.AUTOMATION_MODE || policy.defaultMode;
  const ai = env.AUTOMATION_AI_ENABLED || String(policy.defaultAiEnabled);
  if (!["report-only", "publish"].includes(mode) || !["true", "false"].includes(ai)) {
    throw new Error("Invalid automation mode; use report-only/publish and true/false");
  }
  if (ai === "true") {
    const url = new URL(env.AUTOMATION_API_BASE_URL);
    if (url.protocol !== "https:" || url.username || url.password || url.search || url.hash ||
        !/^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$/.test(env.AUTOMATION_MODEL || "")) {
      throw new Error("AI requires an approved HTTPS endpoint and model");
    }
  }
  return { mode, aiEnabled: ai === "true" };
}

function safePath(value) {
  return typeof value === "string" && value.length > 0 && value.length <= 1024 &&
    !/[\u0000-\u001f\u007f\\:]/.test(value) && !value.startsWith("/") &&
    !value.split("/").some((part) => part === "" || part === "." || part === ".." ||
      part.toLowerCase() === ".git" || /[. ]$/.test(part) ||
      /^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(part));
}

function globRegex(glob) {
  // GitHub path filters allow ** across separators, including in patterns such as **.cs.
  let result = "^";
  for (let i = 0; i < glob.length; i++) {
    const char = glob[i];
    if (char === "*") {
      if (glob[i + 1] === "*") {
        i++;
        if (glob[i + 1] === "/") { i++; result += "(?:.*/)?"; }
        else result += ".*";
      } else result += "[^/]*";
    } else if (char === "?") result += "[^/]";
    else if ("[]+!(){}".includes(char)) throw new Error("Unsupported CI filter syntax");
    else result += char.replace(/[\\^$.|]/g, "\\$&");
  }
  return new RegExp(`${result}$`);
}

function matchesFilters(value, patterns) {
  let matched = false;
  for (const entry of patterns) {
    const negative = entry.startsWith("!");
    if (globRegex(negative ? entry.slice(1) : entry).test(value)) matched = !negative;
  }
  return matched;
}

function workflows(root, baseRef, files) {
  return policy.workflows.filter((entry) => {
    const parsed = YAML.parse(fs.readFileSync(path.join(root, ".github", "workflows", entry.file), "utf8"));
    if (parsed.name !== entry.name) throw new Error("CI workflow identity changed");
    const trigger = parsed.on?.pull_request;
    if (!trigger || trigger["paths-ignore"] || trigger["branches-ignore"] || trigger.tags ||
        trigger.types) throw new Error("Unsupported CI trigger; update automation policy");
    if (trigger.branches && !matchesFilters(baseRef, trigger.branches)) return false;
    return !trigger.paths || files.some((file) =>
      [file.filename, file.previous_filename].filter(Boolean).some((name) => matchesFilters(name, trigger.paths)));
  });
}

function analyze(pr, files) {
  if (files.length !== pr.changed_files || files.length > policy.maxFiles ||
      files.some((file) => !safePath(file.filename) ||
        (file.previous_filename && !safePath(file.previous_filename)) ||
        !Number.isSafeInteger(file.additions) || !Number.isSafeInteger(file.deletions) ||
        file.additions < 0 || file.deletions < 0)) {
    throw new Error("Incomplete or unsafe changed-file manifest");
  }
  const scopes = new Set();
  const managers = new Set();
  const selected = new Set();
  let lines = 0;
  for (const file of files) {
    lines += file.additions + file.deletions;
    if (!Number.isSafeInteger(lines)) throw new Error("Changed-line count exceeds safe arithmetic");
    for (const name of [file.filename, file.previous_filename].filter(Boolean)) {
      if (/^src\/UniGetUI\.Core\./.test(name)) scopes.add("core");
      if (/^src\/UniGetUI\.PackageEngine\./.test(name)) {
        scopes.add("package-engine"); selected.add("package-engine");
      }
      const manager = /^src\/UniGetUI\.PackageEngine\.Managers\.([^/]+)\//.exec(name);
      if (manager) { scopes.add("managers"); managers.add(manager[1]); }
      if (/^src\/UniGetUI\.Avalonia\//.test(name)) scopes.add("ui");
      if (/^src\/UniGetUI\.Tui/.test(name)) scopes.add("tui");
      if (/^src\/(?:UniGetUI\.(?:Interface\.IpcApi|CliLauncher|AgentPolicy\.ElevatedHelper)|WindowsPackageManager\.Interop)\//.test(name) ||
          /^src\/UniGetUI\.PackageEngine\.AgentBroker\//.test(name)) scopes.add("cli-ipc");
      if (/^(?:src\/Languages\/|scripts\/translation\/|\.agents\/skills\/translation-)/.test(name)) scopes.add("localization");
      if (/^(?:\.github\/|scripts\/|package\/|InstallerExtras\/|global\.json$|UniGetUI\.iss$)/.test(name) ||
          /\.(?:csproj|props|targets|slnx?)$/.test(name)) scopes.add("tooling");
      if (/^(?:docs\/|[^/]+\.md$)/.test(name)) scopes.add("docs");
      if (/\.(?:cs|csproj|props|targets|slnx?|axaml)$/.test(name) || name === "global.json") {
        selected.add("dotnet-aot");
      }
      if (/^src\/UniGetUI\.(?:Avalonia|Tui)/.test(name) || /\.(?:axaml|resx)$/.test(name) ||
          /^(?:src\/Languages\/|scripts\/translation\/|\.agents\/skills\/translation-)/.test(name)) {
        selected.add("ui-localization");
      }
      if (/^(?:\.github\/|scripts\/|package\/|InstallerExtras\/)/.test(name) ||
          /^(?:AGENTS\.md$|CLAUDE\.md$|CONTRIBUTING\.md$|global\.json$|\.agents\/skills\/)/.test(name) ||
          /\.(?:props|targets|csproj|iss)$/.test(name) ||
          /(?:IpcApi|AgentBroker|ElevatedHelper|SecureSettings|Interop|Download|Program\.cs)/.test(name)) {
        selected.add("skeptical");
      }
      if (/\.(?:cs|js|ts|ps1|sh|yml|yaml|csproj|props|targets)$/.test(name)) selected.add("code-compressor");
    }
  }
  const size = policy.size.find((entry) => entry.lines === null ||
    (lines <= entry.lines && files.length <= entry.files)).label;
  const labels = [...scopes].sort().map((scope) => `scope/${scope}`);
  labels.push(size);
  if (pr.author_association === "FIRST_TIME_CONTRIBUTOR" || pr.author_association === "FIRST_TIMER") {
    labels.push("contributor/first-time");
  }
  return {
    scopes: [...scopes].sort(), managers: [...managers].sort(), files: files.length, lines, labels, risk: "unknown",
    reviewers: REVIEWERS.filter((id) => selected.has(id)),
  };
}

function route(deterministic, classifier) {
  const selected = new Set([...deterministic.reviewers, ...classifier.reviewers]);
  if (classifier.risk !== "low") selected.add("skeptical");
  if ([...selected].some((id) => !REVIEWERS.includes(id))) throw new Error("Unknown reviewer");
  return REVIEWERS.filter((id) => selected.has(id));
}

module.exports = { ROOT, policy, SHA, REVIEWERS, modes, safePath, globRegex, matchesFilters, workflows, analyze, route };
