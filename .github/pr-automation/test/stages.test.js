"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { selectReviewers, aggregate, finish } = require("../stages");
const { validReport } = require("../publication");
const { snapshot, report, finding } = require("./fixtures");

test("classification, specialist aggregate and final report round-trip through actual artifact files", () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "unigetui-stages-"));
  const cwd = process.cwd();
  try {
    process.chdir(temp);
    fs.mkdirSync("pr-evidence");
    fs.mkdirSync(".openai-agent-output");
    fs.mkdirSync("specialist-results");
    const input = snapshot(), output = report(input);
    output.classifier.reviewers = ["skeptical"];
    fs.writeFileSync("pr-evidence/context.json", JSON.stringify(output.context));
    fs.writeFileSync("pr-evidence/snapshot.json", JSON.stringify(input));
    fs.writeFileSync(".openai-agent-output/classifier.json", JSON.stringify(output.classifier));
    assert.deepEqual(selectReviewers(), ["skeptical"]);
    const candidate = { ...output.review, reviewer: "skeptical", findings: [finding()] };
    fs.writeFileSync("specialist-results/skeptical.json", JSON.stringify(candidate));
    aggregate("specialist-results");
    const final = { ...output.review, findings: [finding({ sources: ["skeptical:a-finding"] })],
      dispositions: [{ candidate: "skeptical:a-finding", decision: "refined", reason: "Confirmed and narrowed." }] };
    fs.writeFileSync(".openai-agent-output/general.json", JSON.stringify(final));
    finish("123");
    const result = JSON.parse(fs.readFileSync("automation-results/result-42.json", "utf8"));
    assert.equal(validReport(result, input), result);
    assert.equal(result.specialists.length, 1);
    assert.equal(result.review.findings[0].sources[0], "skeptical:a-finding");
    fs.writeFileSync("specialist-results/skeptical.json", JSON.stringify({ ...candidate, head: "e".repeat(40) }));
    assert.throws(() => aggregate("specialist-results"), /head/);
    fs.unlinkSync("specialist-results/skeptical.json");
    assert.throws(() => aggregate("specialist-results"), /ENOENT/);
  } finally {
    process.chdir(cwd);
    fs.rmSync(temp, { recursive: true });
  }
});
