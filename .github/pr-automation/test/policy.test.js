"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const { modes, safePath, analyze, route, workflows, matchesFilters, policy } = require("../policy");
const { file, pr } = require("./fixtures");
const root = path.resolve(__dirname, "..", "..", "..");

test("absent mode makes no provider or mutation decision", () => {
  assert.deepEqual(modes({}), { mode: "report-only", aiEnabled: false });
  assert.deepEqual(modes({ AUTOMATION_API_BASE_URL: "invalid" }), { mode: "report-only", aiEnabled: false });
});
test("invalid modes and unapproved provider shapes fail visibly", () => {
  for (const env of [
    { AUTOMATION_MODE: "on" }, { AUTOMATION_AI_ENABLED: "yes" },
    { AUTOMATION_AI_ENABLED: "true" },
    { AUTOMATION_AI_ENABLED: "true", AUTOMATION_API_BASE_URL: "http://host/v1", AUTOMATION_MODEL: "model" },
    { AUTOMATION_AI_ENABLED: "true", AUTOMATION_API_BASE_URL: "https://name:secret@host/v1", AUTOMATION_MODEL: "model" },
  ]) assert.throws(() => modes(env));
});
test("path validation rejects escapes, Windows devices and control characters", () => {
  for (const value of ["../a", "/a", "a\\b", "a/./b", "C:/a", "a//b", "a\nb", "", "CON", "src/aux.cs",
    "src/a.", "src/a ", "src/.GIT/config"]) assert.equal(safePath(value), false);
  assert.equal(safePath("src/UniGetUI.Core.Tools/Tools.cs"), true);
});
test("GitHub glob behavior covers recursive suffixes, zero directory segments and ordered exclusions", () => {
  assert.equal(matchesFilters("src/A.cs", ["**.cs"]), true);
  assert.equal(matchesFilters("src/A.cs", ["src/**/*.cs"]), true);
  assert.equal(matchesFilters("src/A.cs", ["**.cs", "!src/**"]), false);
  assert.equal(matchesFilters("src/A.cs", ["**.cs", "!src/**", "src/A.cs"]), true);
});
test("docs-only changes require no filtered code CI", () => {
  assert.deepEqual(workflows(root, "main", [file()]), []);
});
test("code changes require both .NET and all CLI matrix CI", () => {
  assert.deepEqual(workflows(root, "main", [file("src/UniGetUI.Core.Tools/Tools.cs")]).map((entry) => entry.file),
    ["dotnet-test.yml", "cli-headless-e2e.yml"]);
});
test("locales require only translation CI; skill scripts and reference data match current triggers", () => {
  for (const name of ["src/Languages/lang_fr.json", ".agents/skills/translation-review/scripts/review.ps1",
    "src/UniGetUI.Core.Data/Assets/Data/Contributors.list"]) {
    assert.deepEqual(workflows(root, "main", [file(name)]).map((entry) => entry.file), ["translation-validation.yml"]);
  }
});
test("workflow changes require automation CI and relevant existing CI; renames cannot evade code checks", () => {
  assert.deepEqual(workflows(root, "main", [file(".github/workflows/dotnet-test.yml")]).map((entry) => entry.file),
    ["dotnet-test.yml", "pr-automation-tests.yml"]);
  assert.deepEqual(workflows(root, "main", [{ ...file(), previous_filename: "src/Before.cs" }]).map((entry) => entry.file),
    ["dotnet-test.yml", "cli-headless-e2e.yml"]);
  assert.deepEqual(workflows(root, "not-main", [file("src/A.cs")]), []);
});
test("size thresholds use both files and lines, with no hidden generated or translation exclusion", () => {
  for (const [lines, count, label] of [[49, 2, "XS"], [50, 2, "S"], [1, 3, "S"], [199, 5, "S"],
    [200, 5, "M"], [449, 10, "M"], [450, 10, "L"], [899, 20, "L"], [900, 20, "XL"],
    [1299, 49, "XL"], [1300, 1, "XXL"], [1, 50, "XXL"]]) {
    const files = Array.from({ length: count }, (_, i) => file(`src/Languages/generated-${i}.json`, i === 0 ? lines : 0));
    assert.equal(analyze(pr({ changed_files: count }), files).labels.includes(`size/${label}`), true);
  }
});
test("incomplete or unsafe manifests cannot look like a low-risk change", () => {
  assert.throws(() => analyze(pr({ changed_files: 2 }), [file()]), /Incomplete/);
  assert.throws(() => analyze(pr(), [file("../escape")]), /unsafe/);
  assert.throws(() => analyze(pr(), [file("a", -1)]), /unsafe/);
});
test("deterministic domain routing cannot be removed by classification", () => {
  const files = [file("src/UniGetUI.PackageEngine.Managers.Pip/Pip.cs"), file("src/UniGetUI.Tui/Views/Page.axaml"),
    file("src/UniGetUI.Interface.IpcApi/Protocol.cs")];
  const result = analyze(pr({ changed_files: 3, author_association: "FIRST_TIME_CONTRIBUTOR" }), files);
  assert.deepEqual(result.managers, ["Pip"]);
  assert.ok(result.labels.includes("contributor/first-time"));
  assert.deepEqual(route(result, { risk: "low", reviewers: [] }), policy.reviewers);
});
test("unknown risk requires skeptical scrutiny without adding every domain", () => {
  assert.deepEqual(route({ reviewers: [] }, { risk: "unknown", reviewers: [] }), ["skeptical"]);
});
test("trusted methodology and executable build surfaces always require skeptical review", () => {
  for (const name of ["AGENTS.md", ".agents/skills/skeptical-reviewer/SKILL.md", "UniGetUI.iss",
    "src/Directory.Build.props", "src/Project.csproj", "global.json"]) {
    assert.equal(analyze(pr(), [file(name)]).reviewers.includes("skeptical"), true, name);
  }
});
