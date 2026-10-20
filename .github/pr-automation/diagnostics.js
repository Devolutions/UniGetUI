"use strict";

const fs = require("node:fs");
const profiles = require("./agents/profiles.json");
const stage = process.argv[2];
if (!profiles[stage]) throw new Error("Unknown diagnostic stage");
const raw = process.env.DIAGNOSTICS;
if (raw && Buffer.byteLength(raw) > 32768) throw new Error("Stage diagnostics exceed limits");
const diagnostics = raw ? JSON.parse(raw) : null;
fs.mkdirSync("diagnostics", { recursive: true });
fs.writeFileSync(`diagnostics/${stage}.json`, JSON.stringify({
  stage, outcome: process.env.STAGE_OUTCOME || "not-started",
  diagnostics, usageComplete: diagnostics?.usage?.complete ?? null,
}));
