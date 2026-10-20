"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { buildIssueRecord, enrichBatchRecords, buildDigest } = require("../evidence");

const OPEN_PRS = [
  {
    number: 5545,
    title: "fix(headless): initialize UniGetUI elevator in headless daemon mode",
    body: "Fixes #5541 by calling LoadElevatorAsync in HeadlessDaemonHost.",
    html_url: "https://github.com/Devolutions/UniGetUI/pull/5545",
  },
  {
    number: 5546,
    title: "fix(avalonia): restore Tab and Shift+Tab keyboard traversal in settings",
    body: "Closes #5543",
    html_url: "https://github.com/Devolutions/UniGetUI/pull/5546",
  },
];

const REPRESENTATIVE_ISSUES = [
  {
    number: 5541,
    title: "[BUG] Headless daemon fails to initialize UniGetUI Elevator",
    labels: [{ name: "bug" }],
    user: { login: "headlessuser" },
    body: "### UniGetUI Version\n2026.3.2\n### Windows version, edition, and architecture\nWindows 11 24H2 x64\n### Describe your issue\nRunning `UniGetUI --headless` does not call LoadElevatorAsync in HeadlessDaemonHost so elevated operations fail.",
    comments: [],
  },
  {
    number: 5543,
    title: "[BUG] Keyboard focus and Tab traversal broken in Avalonia settings",
    labels: [{ name: "bug" }],
    user: { login: "a11yuser" },
    body: "### UniGetUI Version\n2026.3.2\n### Windows version, edition, and architecture\nWindows 11 24H2 x64\n### Describe your issue\nScreen reader NVDA and keyboard navigation with Tab / Shift+Tab cannot traverse SecureCheckboxCard.",
    comments: [],
  },
  {
    number: 5470,
    title: "[BUG] WinGet package fails to install when custom install location is configured",
    labels: [{ name: "bug" }],
    user: { login: "locuser" },
    body: "### UniGetUI Version\n2026.3.1\n### Windows version, edition, and architecture\nWindows 11 x64\n### Describe your issue\nWorks in terminal without `--location`, fails in UniGetUI.\n### Package Managers Logs\nExecutable: winget.exe\nArguments: install --id Foo.Bar --exact --source winget --location \"D:\\Apps\\Foo\"",
    comments: [],
  },
  {
    number: 5218,
    title: "[BUG] Package update fails in UniGetUI but `winget install` works in PowerShell",
    labels: [{ name: "bug" }, { name: "waiting-for-user-feedback" }],
    user: { login: "reporter5218" },
    body: "### UniGetUI Version\n3.3.6\n### Windows version, edition, and architecture\nWindows 11 x64\n### Describe your issue\nRunning `> winget install Some.App` in terminal works, but UniGetUI fails with pinget.\n### Package Managers Logs\nwinget.exe\" update --id Some.App --silent --force",
    comments: [
      { id: 10, user: { login: "marticliment", type: "User" }, created_at: "2026-02-01T10:00:00Z", body: "Does running `winget update` work?" },
      { id: 11, user: { login: "reporter5218", type: "User" }, created_at: "2026-02-02T10:00:00Z", body: "I tested and posted the output above." },
    ],
  },
  {
    number: 5188,
    title: "[CRASH] UniGetUI crashes on startup before main window",
    labels: [{ name: "bug" }, { name: "important" }, { name: "needs-author-answer" }],
    user: { login: "crashuser" },
    body: "### UniGetUI Version\n2026.3.2\n### Windows version, edition, and architecture\nWindows 11 x64\n### Describe your crash\nApp is not launching and hard-crashing on startup with NullReferenceException.",
    comments: [],
  },
  {
    number: 5058,
    title: "[BUG] Scoop package icon not loading",
    labels: [{ name: "bug" }, { name: "released" }],
    user: { login: "scoopuser" },
    body: "### UniGetUI Version\n3.3.5\n### Windows version, edition, and architecture\nWindows 11 x64\n### Describe your issue\nScoop icons fail to load.",
    comments: [
      { id: 20, user: { login: "marticliment", type: "User" }, created_at: "2026-01-10T10:00:00Z", body: "Fixed in the latest release." },
      { id: 21, user: { login: "otheruser", type: "User" }, created_at: "2026-01-15T10:00:00Z", body: "The bug persists and I still have this issue on the new release." },
    ],
  },
  {
    number: 4866,
    title: "[BUG] WinGet COM Server throws 0x8A15000F when checking for updates",
    labels: [{ name: "bug" }],
    user: { login: "comuser1" },
    body: "### UniGetUI Version\n3.3.6\n### Windows version, edition, and architecture\nWindows 11 x64\n### Describe your issue\nWinGet COM Server fails with 0x8A15000F during update check for --id Microsoft.PowerToys.",
    comments: [],
  },
  {
    number: 4859,
    title: "[BUG] WinGet COM Server error 0x8A15000F on Installed Packages tab",
    labels: [{ name: "bug" }],
    user: { login: "comuser2" },
    body: "### UniGetUI Version\n2026.3.2\n### Windows version, edition, and architecture\nWindows 11 x64\n### Describe your issue\nWindowsPackageManagerServer.exe fails with 0x8A15000F when listing --id Microsoft.PowerToys.",
    comments: [],
  },
  {
    number: 4412,
    title: "[FEATURE REQUEST] Official Linux support (Flatpak / Flathub / Apt / DNF)",
    labels: [{ name: "new-feature" }],
    user: { login: "linuxfan" },
    body: "### Describe the new feature\nPlease port UniGetUI to Linux and publish on Flathub / Flatpak.",
    comments: [],
  },
  {
    number: 4387,
    title: "[FEATURE REQUEST] macOS cross-platform desktop build",
    labels: [{ name: "new-feature" }],
    user: { login: "macfan" },
    body: "### Describe the new feature\nPlease provide a native macOS version of UniGetUI.",
    comments: [],
  },
  {
    number: 4233,
    title: "[FEATURE REQUEST] CLI support for managing packages over SSH",
    labels: [{ name: "new-feature" }],
    user: { login: "tuiuser" },
    body: "### Describe the new feature\nAdd CLI support and `uniget tui` mode for terminal users.",
    comments: [],
  },
  {
    number: 4115,
    title: "[BUG] Chocolatey elevation with gsudo prompts twice",
    labels: [{ name: "bug" }],
    user: { login: "chocuser" },
    body: "### UniGetUI Version\n3.3.6\n### Windows version, edition, and architecture\nWindows 11 x64\n### Describe your issue\nWhen updating Chocolatey packages with gsudo administrator elevation, UAC prompt appears twice.",
    comments: [],
  },
  {
    number: 3605,
    title: "[ENHANCEMENT] Scheduled install time and monthly update check",
    labels: [{ name: "enhancement" }],
    user: { login: "scheduser" },
    body: "### UniGetUI Version\n2016.1\n### Describe the improvement\nAllow scheduled install time and 1x/monthly schedule update checks.",
    comments: [],
  },
  {
    number: 3580,
    title: "[FEATURE REQUEST] CLI flag `UniGetUI file.ubundle --install --no-ui`",
    labels: [{ name: "new-feature" }],
    user: { login: "bundlecli" },
    body: "### UniGetUI Version\n3.1.2-beta0\n### Describe the new feature\nSupport headless install of `.ubundle` via `UniGetUI file --install --no-ui`.\n### UniGetUI Log\nWelcome to UniGetUI Version 3.1.1",
    comments: [],
  },
  {
    number: 2407,
    title: "[FEATURE REQUEST] Native ARM64 version for Windows on Snapdragon",
    labels: [{ name: "new-feature" }],
    user: { login: "armuser" },
    body: "### Describe the new feature\nPlease provide a native ARM64 version for Windows ARM64.",
    comments: [
      { id: 30, user: { login: "armuser", type: "User" }, created_at: "2026-03-01T10:00:00Z", body: "Thanks for the ARM64 build, though WinGet is still overriding arm64 with x64 on some updates." },
    ],
  },
  {
    number: 1767,
    title: "[BUG] vcpkg update says vcpkg.exe is gone and deletes vcpkg.exe",
    labels: [{ name: "bug" }],
    user: { login: "vcpkguser" },
    body: "### UniGetUI Version\n3.3.6\n### Windows version, edition, and architecture\nWindows 11 x64\n### Describe your issue\nbootstrap-vcpkg.bat failed and now vcpkg.exe is gone.",
    comments: [],
  },
  {
    number: 1556,
    title: "[BUG] German translation cut off in Settings dialog",
    labels: [{ name: "bug" }],
    user: { login: "locde" },
    body: "### UniGetUI Version\n2026.3.2\n### Windows version, edition, and architecture\nWindows 11 x64\n### Describe your issue\nGerman translation string overflows the button in settings dialog.",
    comments: [],
  },
  {
    number: 1292,
    title: "[FEATURE REQUEST] Run as CLI / headless install with flags to always elevate and cache administrator rights",
    labels: [{ name: "new-feature" }],
    user: { login: "admincli" },
    body: "### Describe the new feature\nPlease allow UniGetUI to run as CLI with flags to always elevate and disable any possible UAC prompt.",
    comments: [],
  },
];

test("replay benchmark across 18 representative UniGetUI issues verifies routing, PR matching, capabilities, and queues", () => {
  const fullRecords = REPRESENTATIVE_ISSUES.map((issue) =>
    buildIssueRecord(issue, { comments: issue.comments, events: [], openPrs: OPEN_PRS }),
  );
  enrichBatchRecords(fullRecords);
  const byNumber = new Map(fullRecords.map((r) => [r.analysis.number, r]));

  const r5541 = byNumber.get(5541);
  assert.ok(r5541.analysis.proposedLabels.includes("area/elevation-agent"));
  assert.equal(r5541.candidatePrs[0].number, 5545);

  const r5543 = byNumber.get(5543);
  assert.ok(r5543.analysis.proposedLabels.includes("area/accessibility"));
  assert.equal(r5543.candidatePrs[0].number, 5546);

  const r5470 = byNumber.get(5470);
  assert.equal(r5470.analysis.commandComparison.hasComparison, true);
  assert.equal(r5470.analysis.commandComparison.equivalent, false);

  const r5218 = byNumber.get(5218);
  assert.equal(r5218.discussionState.authorReplied, true);
  assert.equal(r5218.analysis.backend, "pinget-or-bundled");
  assert.equal(r5218.analysis.commandComparison.equivalent, false);

  const r5188 = byNumber.get(5188);
  assert.equal(r5188.analysis.missingDiagnostics.length, 0);

  const r5058 = byNumber.get(5058);
  assert.equal(r5058.discussionState.releaseVerificationNeeded, true);

  const r4866 = byNumber.get(4866);
  assert.ok(r4866.relatedIssues.some((rel) => rel.number === 4859));

  for (const num of [4412, 4387, 4233, 3605, 3580, 2407, 1292]) {
    const rec = byNumber.get(num);
    assert.ok(rec.capabilityMatches.length > 0, `Expected capability match for #${num}`);
    assert.ok(rec.capabilityMatches[0].unmetScope.length > 0);
  }

  const r3605 = byNumber.get(3605);
  assert.equal(r3605.analysis.versionInfo.era, "malformed-or-unknown");
  assert.ok(r3605.analysis.proposedLabels.includes("area/scheduling"));

  const r3580 = byNumber.get(3580);
  assert.equal(r3580.analysis.versionInfo.versionConflict, true);
  assert.ok(r3580.analysis.proposedLabels.includes("area/package-bundles"));

  const r1767 = byNumber.get(1767);
  assert.equal(r1767.analysis.destructiveRisk, true);
  assert.ok(r1767.analysis.proposedLabels.includes("vcpkg"));

  const r1556 = byNumber.get(1556);
  assert.ok(r1556.analysis.proposedLabels.includes("area/localization"));

  const digest = buildDigest(fullRecords);
  assert.ok(digest.queues.currentRegressions.some((i) => i.number === 5541));
  assert.ok(digest.queues.authorReplied.some((i) => i.number === 5218));
  assert.ok(digest.queues.releaseVerification.some((i) => i.number === 5058));
  assert.ok(digest.queues.implementedOrPartial.some((i) => i.number === 4233));
});
