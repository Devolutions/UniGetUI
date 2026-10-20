"use strict";

const crypto = require("node:crypto");
const path = require("node:path");
const policy = require("./policy.json");
const manifest = require("./labels.json");
const ROOT = path.resolve(__dirname, "..", "..");

const SHA = /^[a-f0-9]{40}$/;
const FINGERPRINT = /^[a-f0-9]{64}$/;
const STAGES = policy.stages;
const PROTECTED_LABELS = new Set(policy.protectedLabels);
const OWNED_LABELS = new Set([
  ...policy.typeLabels,
  ...policy.managerLabels,
  ...policy.platformLabels,
  ...policy.areaLabels,
]);
const BOT_LOGINS = new Set(["github-actions[bot]", "dependabot[bot]", "renovate[bot]"]);

const MANAGER_PATTERNS = [
  ["winget", /\b(?:winget|pinget|windowspackagemanager|microsoft\.desktopappinstaller|0x8a15[0-9a-f]{4})\b/i],
  ["scoop", /\b(?:scoop|scoopinstaller)\b/i],
  ["chocolatey", /\b(?:choco|chocolatey)\b/i],
  ["powershell", /\b(?:psgallery|powershellget|psresourceget|get-psrepository|register-psrepository|install-module|pwsh)\b/i],
  ["vcpkg", /\b(?:vcpkg|vcpkg_root|bootstrap-vcpkg)\b/i],
  ["cargo", /\b(?:cargo|cargo-binstall| cargo-update|crates\.io)\b/i],
  ["pip", /\b(?:pip|pip3|pypi)\b/i],
  ["npm", /\b(?:npm|nodejs|fnm)\b/i],
  ["bun", /\b(?:bun|bun_install|bunfig)\b/i],
  ["dotnet-tool", /\b(?:dotnet\s+tool|dotnet-tool)\b/i],
  ["homebrew", /\b(?:brew|homebrew)\b/i],
  ["apt", /\b(?:apt|apt-get)\b/i],
  ["dnf", /\b(?:dnf)\b/i],
  ["pacman", /\b(?:pacman)\b/i],
  ["flatpak", /\b(?:flatpak|flathub)\b/i],
  ["snap", /\b(?:snap|snapcraft)\b/i],
];

const MALFORMED_VERSIONS = new Set(["2016.1", "2026.20", "20.26", "25.2", "1.6", "3.16", "3.5"]);

function modes(env = {}) {
  const mode = env.ISSUE_AUTOMATION_MODE || policy.defaultMode;
  const ai = env.ISSUE_AUTOMATION_AI_ENABLED || String(policy.defaultAiEnabled);
  const rawBudget = env.ISSUE_AUTOMATION_DAILY_BUDGET ?? String(policy.defaultDailyBudget);
  const dailyBudget = Number(rawBudget);
  if (!["report-only", "publish-labels"].includes(mode) || !["true", "false"].includes(ai) ||
      !/^\d+$/.test(String(rawBudget)) || !Number.isSafeInteger(dailyBudget) ||
      dailyBudget < 0 || dailyBudget > policy.maxDailyBudget) {
    throw new Error("Invalid issue automation mode, AI flag, or daily budget");
  }
  if (ai === "true") {
    if (dailyBudget <= 0) {
      throw new Error("AI issue triage requires a positive finite ISSUE_AUTOMATION_DAILY_BUDGET");
    }
    const url = new URL(env.ISSUE_AUTOMATION_API_BASE_URL);
    if (url.protocol !== "https:" || url.username || url.password || url.search || url.hash ||
        !/^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$/.test(env.ISSUE_AUTOMATION_MODEL || "")) {
      throw new Error("AI issue triage requires an approved HTTPS endpoint and model");
    }
  }
  return { mode, aiEnabled: ai === "true", dailyBudget };
}

function isBotUser(user) {
  if (!user) return false;
  return user.type === "Bot" || BOT_LOGINS.has(user.login) || /\[bot\]$/i.test(user.login || "");
}

function redactText(input) {
  if (typeof input !== "string" || !input) return "";
  return input
    .replace(/([A-Za-z]:\\Users\\)([^\\\r\n/]+)(\\)/gi, "$1<redacted-user>$3")
    .replace(/(\/(?:Users|home)\/)([^/\r\n\\]+)(\/)/g, "$1<redacted-user>$3")
    .replace(/(https?:\/\/)([^/\s:@]+:[^/\s@]+@)/gi, "$1<redacted-credentials>@")
    .replace(/([?&](?:token|access_token|api_key|apikey|key|secret|password|sig|signature)=)[^&\s#]+/gi, "$1<redacted>")
    .replace(/\b(Bearer\s+)[A-Za-z0-9._~+/=-]{8,}/gi, "$1<redacted-token>")
    .replace(/\b(gh[pousr]_[A-Za-z0-9_]{16,}|github_pat_[A-Za-z0-9_]{16,})\b/g, "<redacted-github-token>");
}

function curateBody(rawBody, maxBytes = policy.maxBodyBytes) {
  const text = typeof rawBody === "string" ? rawBody : "";
  const rawBodyBytes = Buffer.byteLength(text, "utf8");
  const redacted = redactText(text);
  const redactedBytes = Buffer.byteLength(redacted, "utf8");
  if (redactedBytes <= maxBytes) {
    return { text: redacted, rawBodyBytes, curatedBytes: redactedBytes, truncated: false, omittedBytes: 0 };
  }
  const headBytes = Math.floor(maxBytes * 0.65);
  const tailBytes = Math.max(0, maxBytes - headBytes - 128);
  const buffer = Buffer.from(redacted, "utf8");
  const head = buffer.subarray(0, headBytes).toString("utf8");
  const tail = buffer.subarray(buffer.length - tailBytes).toString("utf8");
  const omittedBytes = Math.max(0, rawBodyBytes - (headBytes + tailBytes));
  const marker = `\n\n[... ${omittedBytes} bytes omitted by deterministic evidence limit ...]\n\n`;
  const combined = `${head}${marker}${tail}`;
  return {
    text: combined,
    rawBodyBytes,
    curatedBytes: Buffer.byteLength(combined, "utf8"),
    truncated: true,
    omittedBytes,
  };
}

function parseSections(body) {
  const sections = {};
  if (typeof body !== "string" || !body.trim()) return sections;
  const lines = body.replace(/\r\n/g, "\n").split("\n");
  let current = "_preamble";
  let buffer = [];
  for (const line of lines) {
    const heading = /^###\s+(.+?)\s*$/.exec(line);
    if (heading) {
      if (buffer.length) sections[current] = buffer.join("\n").trim();
      current = heading[1].trim().toLowerCase();
      buffer = [];
    } else {
      buffer.push(line);
    }
  }
  if (buffer.length) sections[current] = buffer.join("\n").trim();
  return sections;
}

function cleanFieldValue(value) {
  if (typeof value !== "string") return null;
  const trimmed = value.trim();
  if (!trimmed || /^_no response_$/i.test(trimmed) || /^not applicable\.?$/i.test(trimmed) || /^n\/a$/i.test(trimmed)) {
    return null;
  }
  return trimmed;
}

function normalizeVersionToken(raw) {
  if (typeof raw !== "string") return null;
  const match = /\b(?:v|version\s*)?(\d{1,4}\.\d{1,2}(?:\.\d{1,3}){0,2}(?:-[A-Za-z0-9.]+)?)\b/i.exec(raw);
  if (match) return match[1];
  if (/\b(?:edge|nightly|dev|main)\b/i.test(raw)) return "edge";
  return null;
}

function classifyVersionEra(rawField, normalized) {
  if (!rawField && !normalized) return "missing";
  if (normalized === "edge") return "edge-or-dev";
  if (!normalized) return "malformed-or-unknown";
  const prefix = normalized.split(".").slice(0, 2).join(".");
  if (MALFORMED_VERSIONS.has(prefix)) return "malformed-or-unknown";
  if (normalized.startsWith(`${policy.currentReleaseSeries}.`) || normalized === policy.currentReleaseSeries) {
    return "current-2026.3";
  }
  if (policy.supportedReleaseSeries.some((series) => normalized.startsWith(`${series}.`) || normalized === series)) {
    return "supported-2026";
  }
  if (/^3\.[0-3](?:\.|$)/.test(normalized)) return "legacy-3.x";
  if (/^2\./.test(normalized)) return "legacy-2.x";
  return "malformed-or-unknown";
}

function extractVersionInfo(sections, body = "", title = "") {
  const rawField = cleanFieldValue(
    sections["unigetui version"] || sections["wingetui version"] || sections["version"] || null
  );
  const logMatch = /Welcome to (?:UniGetUI|WingetUI) Version ([^\r\n]+)/i.exec(body);
  const rawLog = logMatch ? logMatch[1].trim() : null;
  const titleMatch = /\b(?:UniGetUI|WingetUI)\s+v?(2026\.\d+(?:\.\d+)?|3\.\d+(?:\.\d+)?|2\.\d+(?:\.\d+)?)\b/i.exec(title);
  const fieldNormalized = normalizeVersionToken(rawField);
  const logNormalized = normalizeVersionToken(rawLog);
  const titleNormalized = titleMatch ? titleMatch[1] : null;
  const effectiveVersion = fieldNormalized || logNormalized || titleNormalized || null;
  const versionConflict = Boolean(
    fieldNormalized && logNormalized &&
    fieldNormalized.replace(/\.0$/, "") !== logNormalized.replace(/\.0$/, "")
  );
  const uiFrameworkMatch = /UI Framework:\s*([^\r\n]+)/i.exec(body);
  const uiFramework = uiFrameworkMatch
    ? uiFrameworkMatch[1].trim()
    : /\bavalonia\b/i.test(`${title}\n${body}`)
      ? "Avalonia"
      : /\bwinui\s*3?\b/i.test(`${title}\n${body}`)
        ? "WinUI 3"
        : null;
  return {
    rawField,
    rawLog,
    version: effectiveVersion,
    era: classifyVersionEra(rawField || rawLog || titleNormalized, effectiveVersion),
    versionConflict,
    uiFramework,
  };
}

function detectPlatform(sections, body = "", title = "") {
  const platforms = new Set();
  const osField = cleanFieldValue(
    sections["operating system, version, and architecture"] ||
    sections["operating system / platform"] ||
    sections["operating system and architecture"] ||
    sections["windows version, edition, and architecture"] ||
    sections["os / platform"] ||
    null
  );
  const logOsMatch = /OS:\s*([^\r\n]+)/i.exec(body);
  const combined = `${osField || ""}\n${logOsMatch ? logOsMatch[1] : ""}\n${title}`;
  if (/\b(?:windows|win10|win11|10\.0\.\d{4,5}|microsoft windows)\b/i.test(combined)) {
    platforms.add("platform/windows");
  }
  if (/\b(?:linux|ubuntu|debian|fedora|arch\s+linux|opensuse|glibc_\d|\.deb\b|\.rpm\b|flatpak|flathub|snap)\b/i.test(`${combined}\n${body}`)) {
    platforms.add("platform/linux");
  }
  if (/\b(?:macos|mac\s*os|osx|apple\s+silicon|\.dmg\b|\/applications\/unigetui\.app)\b/i.test(`${combined}\n${body}`)) {
    platforms.add("platform/macos");
  }
  const archMatch = /\b(arm64|aarch64|x64|x86_64|amd64)\b/i.exec(`${combined}\n${body}`);
  return {
    osField,
    architecture: archMatch ? archMatch[1].toLowerCase().replace("x86_64", "x64").replace("amd64", "x64").replace("aarch64", "arm64") : null,
    labels: [...platforms].sort(),
  };
}

function detectManagers(sections, body = "", title = "", existingLabels = []) {
  const managers = new Set();
  for (const label of existingLabels) {
    if (policy.managerLabels.includes(label)) managers.add(label);
  }
  const structuredManager = cleanFieldValue(
    sections["affected package manager or backend"] ||
    sections["package manager"] ||
    sections["affected package manager"] ||
    null
  );
  const primaryText = `${title}\n${structuredManager || ""}\n${sections["describe your issue"] || ""}\n${sections["describe the new feature"] || ""}\n${sections["describe the improvement"] || ""}`;
  for (const [manager, pattern] of MANAGER_PATTERNS) {
    if (pattern.test(primaryText)) managers.add(manager);
  }
  // If no manager was identified in the primary description, inspect operation lines in logs (ignoring routine startup 'which' checks).
  if (managers.size === 0) {
    const filteredBody = body
      .split(/\r?\n/)
      .filter((line) => !/Begin "which" search|was not found on the system|Found \d+ (?:available|installed) packages from/i.test(line))
      .join("\n");
    for (const [manager, pattern] of MANAGER_PATTERNS) {
      if (pattern.test(filteredBody)) managers.add(manager);
    }
  }
  return [...managers].filter((name) => policy.managerLabels.includes(name)).sort();
}

function detectBackendAndChannel(sections, body = "") {
  const structuredBackend = cleanFieldValue(
    sections["affected package manager or backend"] ||
    sections["winget / manager backend"] ||
    sections["backend"] ||
    null
  );
  let backend = "unknown";
  if (structuredBackend) {
    if (/pinget|bundled/i.test(structuredBackend)) backend = "pinget-or-bundled";
    else if (/com/i.test(structuredBackend)) backend = "winget-com";
    else if (/cli/i.test(structuredBackend)) backend = "system-cli";
  } else if (/\bpinget\b|bundled in-proc COM/i.test(body)) {
    backend = "pinget-or-bundled";
  } else if (/WindowsPackageManagerServer\.exe|IsCOMCall:1|WinGet COM Server/i.test(body)) {
    backend = "winget-com";
  } else if (/winget\.exe"\s+(?:update|install|upgrade|uninstall)/i.test(body)) {
    backend = "system-cli";
  }
  const structuredChannel = cleanFieldValue(
    sections["installation source / channel"] ||
    sections["installation channel"] ||
    null
  );
  let channel = structuredChannel ? structuredChannel.toLowerCase() : "unknown";
  if (channel === "unknown") {
    if (/Packaged \(MSIX\):\s*True|Microsoft\.DesktopAppInstaller|WindowsApps/i.test(body)) channel = "msix-or-store";
    else if (/portable/i.test(body)) channel = "portable";
  }
  return { backend, channel };
}

function detectAreas(title = "", sections = {}, body = "") {
  const areas = new Set();
  const prose = `${title}\n${sections["describe your issue"] || ""}\n${sections["describe your crash"] || ""}\n${sections["describe the new feature"] || ""}\n${sections["describe the improvement"] || ""}\n${sections["steps to reproduce the issue"] || ""}`;
  if (/\b(?:nvda|narrator|jaws|screen\s*reader|voiceover|accessibility|accessible\s+name|keyboard\s+(?:focus|navigation|shortcut)|tab\s+traversal|shift\+tab|ui\s*automation)\b/i.test(prose)) {
    areas.add("area/accessibility");
  }
  if (/\b(?:--headless|headless\s+(?:mode|daemon|session)|uniget(?:\.exe)?\s+(?:tui|status|package|bundle|manager|operation|app)|unigetui\s+tui|ipc\s+api|--ipc-api-|cli\s+(?:flag|support|command))\b/i.test(prose)) {
    areas.add("area/cli");
  }
  if (/\b(?:\.ubundle\b|package\s+bundle|export\s+bundle|import\s+bundle|cloud\s+backup|local\s+backup)\b/i.test(prose)) {
    areas.add("area/package-bundles");
  }
  if (/\b(?:schedule[d]?\s+(?:install|update|maintenance|check)|active\s+hours|monthly\s+update|daily\/weekly)\b/i.test(prose)) {
    areas.add("area/scheduling");
  }
  if (/\b(?:unigetui\s+elevator|load\s*elevator\s*async|gsudo|devolutions\s+agent|agent\s+broker|uac\s+prompt|run\s+as\s+administrator|elevat(?:e|ed|ion))\b/i.test(`${prose}\n${body}`)) {
    areas.add("area/elevation-agent");
  }
  if (/\b(?:translation|localization|locale|language\s+file|tolgee|translatedtextblock)\b/i.test(prose)) {
    areas.add("area/localization");
  }
  if (/\b(?:window\s+drag|touchscreen|touch\s+drag|high\s+gpu|gpu\s+usage|dark\s+mode|light\s+mode|theme|icon|dialog|scroll|layout|ui\s+ugly|avalonia\s+ui|winui)\b/i.test(prose)) {
    areas.add("area/ui");
  }
  return [...areas].filter((name) => policy.areaLabels.includes(name)).sort();
}

function detectSecuritySensitivity(title = "", body = "") {
  const text = `${title}\n${body}`;
  const matched = [];
  if (/\bCVE-\d{4}-\d{4,7}\b/i.test(text)) matched.push("cve-reference");
  if (/\b(?:remote\s+code\s+execution|arbitrary\s+code\s+execution|privilege\s+escalation|command\s+injection|path\s+traversal\s+vulnerability|security\s+vulnerability)\b/i.test(text)) {
    matched.push("vulnerability-claim");
  }
  if (/\b(?:BEGIN\s+(?:RSA|OPENSSH|EC)\s+PRIVATE\s+KEY|gh[pousr]_[A-Za-z0-9_]{16,}|github_pat_[A-Za-z0-9_]{16,})\b/.test(text)) {
    matched.push("embedded-secret-or-key");
  }
  return {
    sensitive: matched.length > 0,
    reasons: matched,
    reportingUrl: "https://devolutions.net/security/",
  };
}

function detectCommandComparison(body = "") {
  if (typeof body !== "string" || !body) {
    return { hasComparison: false, equivalent: null, note: null };
  }
  const hasAutoUpdate = /\bwinget(?:\.exe)?["']?\s+(?:update|upgrade)\b[^\r\n]*(?:--silent|--force|--location)/i.test(body);
  const hasManualInstall = />\s*winget\s+install\b/i.test(body) && !/>\s*winget\s+(?:update|upgrade)\b/i.test(body);
  if (hasAutoUpdate && hasManualInstall) {
    return {
      hasComparison: true,
      equivalent: false,
      note: "Report compares an automated 'winget update' operation against a manual 'winget install' command rather than the exact same subcommand and flags.",
    };
  }
  const hasLocationFlag = /--location\s+"[^"]+"/i.test(body) && /without\s+`?--location`?|works\s+in\s+terminal/i.test(body);
  if (hasLocationFlag) {
    return {
      hasComparison: true,
      equivalent: false,
      note: "UniGetUI command injects '--location' while the manual terminal comparison omits '--location'.",
    };
  }
  if (/works\s+in\s+(?:terminal|cmd|powershell)|manual\s+command\s+in\s+the\s+terminal/i.test(body)) {
    return {
      hasComparison: true,
      equivalent: true,
      note: "Author reports a terminal command comparison; verify identical executable, scope, elevation, and arguments.",
    };
  }
  return { hasComparison: false, equivalent: null, note: null };
}

function extractFingerprints(body = "", title = "") {
  const text = `${title}\n${body}`;
  const errorCodes = [...new Set([
    ...(text.match(/\b0x[0-9a-fA-F]{8}\b/g) || []).map((v) => v.toUpperCase()),
    ...(text.match(/\bGLIBC_\d+\.\d+\b/g) || []),
    ...(text.match(/\b(?:[A-Za-z]+Exception|ScriptHalted)\b/g) || []),
  ])].slice(0, 10);
  const packageIds = [...new Set([
    ...Array.from(text.matchAll(/--id[=\s]+"?([A-Za-z0-9._-]+\.[A-Za-z0-9._-]+)"?/g), (m) => m[1]),
    ...Array.from(text.matchAll(/Package=([A-Za-z0-9._-]+)/g), (m) => m[1]),
  ])].slice(0, 10);
  return { errorCodes, packageIds };
}

function determineOwnershipHypothesis({ issueKind, managers, areas, body, commandComparison, errorCodes }) {
  if (issueKind !== "bug") {
    return { category: "unknown", confidence: "low", basis: "Feature or enhancement request, not a defect report." };
  }
  if (commandComparison.hasComparison && commandComparison.equivalent === false) {
    return {
      category: "unigetui-integration",
      confidence: "medium",
      basis: commandComparison.note,
    };
  }
  if (/\b(?:LoadElevatorAsync|HeadlessDaemonHost|SecureCheckboxCard|BUN_INSTALL_GLOBAL_DIR|BUN_INSTALL|bootstrap-vcpkg\.bat)\b/.test(body)) {
    return {
      category: "unigetui-integration",
      confidence: "high",
      basis: "Report identifies a concrete UniGetUI code path, control, or command-construction behavior.",
    };
  }
  if (areas.includes("area/accessibility") || areas.includes("area/ui") || areas.includes("area/cli")) {
    return {
      category: "unigetui-integration",
      confidence: "high",
      basis: "Report concerns UniGetUI's user interface, accessibility tree, or CLI/headless host.",
    };
  }
  if (/Attempting to open pinning database|microsoft\/winget-cli\/issues\//i.test(body)) {
    return {
      category: "manager-upstream",
      confidence: "medium",
      basis: "Evidence references an upstream package-manager defect or COM server loop; keep as hypothesis for maintainer confirmation.",
    };
  }
  if (errorCodes.includes("0x8A150014") || /installer hash|Download failed|hash does not match/i.test(body)) {
    return {
      category: "package-manifest-or-installer",
      confidence: "low",
      basis: "Installer or manifest failure indicator present, but requires verifying whether UniGetUI passed extra arguments such as --location.",
    };
  }
  if (managers.length > 0) {
    return {
      category: "unknown",
      confidence: "low",
      basis: "Package manager operation failed; requires comparing identical command, scope, elevation, and backend.",
    };
  }
  return { category: "unknown", confidence: "low", basis: "Insufficient structured diagnostic evidence." };
}

function checkMissingDiagnostics({ issueKind, versionInfo, platform, managers, areas, sections, body }) {
  if (issueKind !== "bug") return [];
  const missing = [];
  if (versionInfo.era === "missing" || versionInfo.era === "malformed-or-unknown") {
    missing.push({
      field: "unigetui-version",
      question: "Which exact UniGetUI version (for example `2026.3.2`) are you running?",
    });
  }
  if (versionInfo.versionConflict) {
    missing.push({
      field: "version-conflict",
      question: `The reported version field (${versionInfo.rawField}) differs from the pasted log header (${versionInfo.rawLog}); please confirm which version reproduced the issue.`,
    });
  }
  if (platform.labels.length === 0) {
    missing.push({
      field: "platform",
      question: "Which operating system, version/distribution, and architecture (x64 or arm64) are you using?",
    });
  }
  const hasCrashOrStartup = /^\[CRASH\]/i.test(sections._title || "") || /\b(?:not launching|hard-crashing|crash on startup)\b/i.test(body);
  if (areas.includes("area/accessibility")) {
    if (!/\b(?:nvda|narrator|jaws|voiceover|orca|keyboard)\b/i.test(body)) {
      missing.push({
        field: "assistive-technology",
        question: "Which screen reader or keyboard navigation sequence reproduces the accessibility issue?",
      });
    }
    return missing;
  }
  if (hasCrashOrStartup) {
    return missing;
  }
  if (managers.length > 0) {
    const hasLogOrOutput = /Executing process with StartInfo|Welcome to (?:UniGetUI|WingetUI)|```/.test(body);
    if (!hasLogOrOutput) {
      missing.push({
        field: "operation-log-excerpt",
        question: "Please share the relevant redacted operation or package-manager log excerpt (and whether the exact same command succeeds in a terminal).",
      });
    }
  }
  return missing;
}

function contentFingerprint(issue, comments = []) {
  const humanLabels = (issue.labels || [])
    .map((l) => (typeof l === "string" ? l : l.name))
    .filter((name) => !OWNED_LABELS.has(name))
    .sort();
  const nonBotComments = comments
    .filter((c) => !isBotUser(c.user || c.author))
    .map((c) => ({
      id: c.id || null,
      author: c.user?.login || c.author?.login || null,
      createdAt: c.created_at || c.createdAt || null,
      body: c.body || "",
    }));
  const payload = JSON.stringify({
    number: issue.number,
    title: issue.title || "",
    body: issue.body || "",
    state: issue.state || "open",
    humanLabels,
    nonBotComments,
  });
  return crypto.createHash("sha256").update(payload, "utf8").digest("hex");
}

function humanRemovedLabels(events = []) {
  const state = new Map();
  for (const event of events) {
    const name = event.label?.name;
    if (!name) continue;
    if (event.event === "unlabeled" && !isBotUser(event.actor)) {
      state.set(name, true);
    } else if (event.event === "labeled" && !isBotUser(event.actor)) {
      state.set(name, false);
    }
  }
  return new Set([...state.entries()].filter(([, removed]) => removed).map(([name]) => name));
}

function inferTypeLabels(title = "", existingLabels = []) {
  const types = new Set(existingLabels.filter((name) => policy.typeLabels.includes(name)));
  if (/^\[WIDGETS BUG\]/i.test(title)) {
    types.add("bug");
    types.add("widgets-for-unigetui");
  } else if (/^\[(?:BUG|CRASH)\]/i.test(title)) {
    types.add("bug");
  } else if (/^\[FEATURE REQUEST\]/i.test(title)) {
    types.add("new-feature");
  } else if (/^\[(?:IMPROVEMENT|ENHANCEMENT)\]/i.test(title)) {
    types.add("enhancement");
  }
  return [...types].sort();
}

function analyzeIssue(issue, comments = [], events = []) {
  if (!issue || !Number.isSafeInteger(issue.number) || issue.number <= 0) {
    throw new Error("Invalid issue identity");
  }
  if (issue.pull_request) {
    throw new Error("Pull requests are not eligible for issue triage");
  }
  const existingLabels = (issue.labels || []).map((l) => (typeof l === "string" ? l : l.name));
  const title = issue.title || "";
  const rawBody = issue.body || "";
  const curated = curateBody(rawBody);
  const sections = parseSections(curated.text);
  sections._title = title;

  const versionInfo = extractVersionInfo(sections, curated.text, title);
  const platform = detectPlatform(sections, curated.text, title);
  const managers = detectManagers(sections, curated.text, title, existingLabels);
  const { backend, channel } = detectBackendAndChannel(sections, curated.text);
  const areas = detectAreas(title, sections, curated.text);
  const typeLabels = inferTypeLabels(title, existingLabels);
  const issueKind = typeLabels.includes("bug")
    ? "bug"
    : typeLabels.includes("new-feature") || typeLabels.includes("enhancement")
      ? "feature-or-enhancement"
      : "unclassified";

  const security = detectSecuritySensitivity(title, rawBody);
  const commandComparison = detectCommandComparison(curated.text);
  const { errorCodes, packageIds } = extractFingerprints(curated.text, title);
  const ownershipHypothesis = determineOwnershipHypothesis({
    issueKind,
    managers,
    areas,
    body: curated.text,
    commandComparison,
    errorCodes,
  });
  const missingDiagnostics = checkMissingDiagnostics({
    issueKind,
    versionInfo,
    platform,
    managers,
    areas,
    sections,
    body: curated.text,
  });

  const removedByHuman = humanRemovedLabels(events);
  const candidateLabels = new Set([
    ...typeLabels,
    ...platform.labels,
    ...managers,
    ...areas,
  ]);
  const proposedLabels = [...candidateLabels]
    .filter((name) => OWNED_LABELS.has(name) && !PROTECTED_LABELS.has(name) && !removedByHuman.has(name))
    .sort();
  const labelsToAdd = proposedLabels.filter((name) => !existingLabels.includes(name));
  const protectedPresent = existingLabels.filter((name) => PROTECTED_LABELS.has(name)).sort();
  const destructiveRisk = /\b(?:vcpkg\.exe\s+is\s+gone|delete[ds]?\s+vcpkg\.exe|reinstall\s+software\s+removed|100\s*GB|disk\s+space)\b/i.test(`${title}\n${curated.text}`);

  return {
    number: issue.number,
    title: redactText(title),
    state: issue.state || "open",
    author: issue.user?.login || issue.author?.login || null,
    createdAt: issue.created_at || issue.createdAt || null,
    updatedAt: issue.updated_at || issue.updatedAt || null,
    fingerprint: contentFingerprint(issue, comments),
    issueKind,
    existingLabels: [...existingLabels].sort(),
    protectedLabels: protectedPresent,
    removedByHuman: [...removedByHuman].sort(),
    proposedLabels,
    labelsToAdd,
    versionInfo,
    platform,
    managers,
    backend,
    channel,
    areas,
    errorCodes,
    packageIds,
    commandComparison,
    ownershipHypothesis,
    missingDiagnostics,
    security,
    destructiveRisk,
    evidenceBounds: {
      rawBodyBytes: curated.rawBodyBytes,
      curatedBytes: curated.curatedBytes,
      bodyTruncated: curated.truncated,
      omittedBytes: curated.omittedBytes,
      commentCount: comments.length,
      commentsComplete: typeof issue.comments !== "number" || comments.length >= issue.comments,
    },
  };
}

module.exports = {
  ROOT,
  policy,
  manifest,
  SHA,
  FINGERPRINT,
  STAGES,
  PROTECTED_LABELS,
  OWNED_LABELS,
  isBotUser,
  modes,
  redactText,
  curateBody,
  parseSections,
  extractVersionInfo,
  detectPlatform,
  detectManagers,
  detectBackendAndChannel,
  detectAreas,
  detectSecuritySensitivity,
  detectCommandComparison,
  extractFingerprints,
  contentFingerprint,
  humanRemovedLabels,
  analyzeIssue,
};
