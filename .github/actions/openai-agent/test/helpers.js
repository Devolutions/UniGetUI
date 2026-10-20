"use strict";

const fs = require("node:fs");
const path = require("node:path");
const { after } = require("node:test");

const scratchRoot = fs.mkdtempSync(path.join(__dirname, "..", ".test-workspaces-"));

after(() => {
  fs.rmSync(scratchRoot, { recursive: true, force: true });
});

function scratchWorkspace() {
  const directory = fs.mkdtempSync(path.join(scratchRoot, "workspace-"));
  return {
    directory,
    cleanup() {
      fs.rmSync(directory, { recursive: true, force: true });
    },
  };
}

function write(root, relative, content) {
  const target = path.join(root, ...relative.split("/"));
  fs.mkdirSync(path.dirname(target), { recursive: true });
  fs.writeFileSync(target, content);
  return target;
}

module.exports = { scratchWorkspace, write };
