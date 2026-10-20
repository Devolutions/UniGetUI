"use strict";

const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const assert = require("node:assert/strict");

const {
  MAX_LINE_BYTES, MAX_LIST_ENTRIES, MAX_RECURSION_DEPTH, MAX_SEARCH_RESULTS, MAX_SOURCE_FILE_BYTES,
  MAX_TOOL_RESULT_BYTES, MAX_WALK_ENTRIES,
} = require("../src/limits");
const {
  OUTPUT_DIRECTORY, WorkspaceSandbox, boundJson, normalizeRepositoryPath, normalizeToolPath,
  validateOutputFilePath, writeOutputFile,
} = require("../src/sandbox");
const { scratchWorkspace, write } = require("./helpers");

function fixture() {
  const workspace = scratchWorkspace();
  write(workspace.directory, "root/a.txt", "alpha\nneedle here\nomega\n");
  write(workspace.directory, "root/nested/b.txt", "another needle\n");
  write(workspace.directory, "single.txt", "single\n");
  return {
    ...workspace,
    sandbox: new WorkspaceSandbox(workspace.directory, {
      allowedRoots: ["root"],
      allowedFiles: ["single.txt"],
    }),
  };
}

test("read_file enforces capabilities and bounded line ranges", () => {
  const current = fixture();
  try {
    assert.deepEqual(JSON.parse(current.sandbox.readFile({
      path: "root/a.txt", start_line: 2, end_line: 3,
    })), {
      ok: true,
      path: "root/a.txt",
      start_line: 2,
      end_line: 3,
      truncated: true,
      content: "2: needle here\n3: omega",
    });
    assert.match(current.sandbox.readFile({ path: "single.txt" }), /1: single/);
    assert.throws(() => current.sandbox.readFile({ path: "outside.txt" }), /path is unavailable/);
    write(current.directory, "other.txt", "denied");
    assert.throws(() => current.sandbox.readFile({ path: "other.txt" }), /outside allowed capabilities/);
    assert.throws(
      () => current.sandbox.readFile({ path: "root/a.txt", start_line: 2, end_line: 502 }),
      /invalid line range/,
    );
  } finally {
    current.cleanup();
  }
});

test("tool paths drop empty and current-directory segments but stay strict otherwise", () => {
  assert.equal(normalizeToolPath("./root/a.txt"), "root/a.txt");
  assert.equal(normalizeToolPath("root//nested/./b.txt"), "root/nested/b.txt");
  assert.equal(normalizeToolPath("root/"), "root");
  assert.equal(normalizeToolPath("."), "");
  assert.equal(normalizeToolPath("./"), "");
  assert.equal(normalizeToolPath("/"), "/");
  assert.equal(normalizeToolPath("/etc/passwd"), "/etc/passwd");
  const current = fixture();
  try {
    assert.match(current.sandbox.readFile({ path: "./single.txt" }), /1: single/);
    assert.match(current.sandbox.readFile({ path: "root/./nested//b.txt" }), /another needle/);
    assert.equal(JSON.parse(current.sandbox.listFiles({ path: "root/" })).path, "root");
    assert.match(current.sandbox.searchText({ path: "./root", query: "needle" }), /root\/a\.txt/);
    for (const invalid of ["/", "/single.txt", "./../single.txt", "root/../single.txt", "./.git/config"]) {
      assert.throws(() => current.sandbox.readFile({ path: invalid }), /invalid path/, invalid);
    }
    assert.throws(() => current.sandbox.readFile({ path: "." }), /invalid path/);
    assert.throws(() => current.sandbox.searchText({ path: ".", query: "needle" }), /invalid path/);
  } finally {
    current.cleanup();
  }
});

test("listing the workspace root names only the allowed capabilities", () => {
  const current = fixture();
  try {
    write(current.directory, "other.txt", "denied");
    for (const root of [".", "./", ""]) {
      assert.deepEqual(JSON.parse(current.sandbox.listFiles({ path: root, recursive: true })), {
        ok: true,
        path: ".",
        recursive: false,
        truncated: false,
        entries: [
          { path: "root", type: "directory" },
          { path: "single.txt", type: "file" },
        ],
      }, root);
    }
    assert.throws(() => current.sandbox.listFiles({ path: "/" }), /invalid path/);
  } finally {
    current.cleanup();
  }
});

test("repository paths reject absolute, traversal, control, and git paths", () => {
  for (const invalid of [
    "/etc/passwd", "../secret", "root/../secret", "root//a", "root/./a", ".git/config",
    "root/.GIT/config", "C:/Windows/System32", "root\\a.txt", "root/\0bad",
  ]) {
    assert.throws(() => normalizeRepositoryPath(invalid), /invalid path/, invalid);
  }
  assert.equal(normalizeRepositoryPath("root/nested/a.txt"), "root/nested/a.txt");
});

test("structured output files stay in a dedicated non-overwritable regular-file directory", (t) => {
  const current = fixture();
  const external = scratchWorkspace();
  try {
    const output = `${OUTPUT_DIRECTORY}/review.json`;
    assert.equal(validateOutputFilePath(output), output);
    assert.equal(writeOutputFile(current.directory, output, '{"answer":"ok"}'), output);
    assert.equal(fs.readFileSync(path.join(current.directory, output), "utf8"), '{"answer":"ok"}');
    assert.throws(() => writeOutputFile(current.directory, output, "{}"), /output file is unavailable/);
    for (const invalid of [
      "review.json", `${OUTPUT_DIRECTORY}/nested/review.json`, `${OUTPUT_DIRECTORY}/review.txt`,
      `${OUTPUT_DIRECTORY}/../review.json`, ".github/workflows/review.yml",
    ]) {
      assert.throws(() => validateOutputFilePath(invalid), /structured output file|invalid path/, invalid);
    }
    const linkedWorkspace = scratchWorkspace();
    try {
      fs.symlinkSync(external.directory, path.join(linkedWorkspace.directory, OUTPUT_DIRECTORY),
        process.platform === "win32" ? "junction" : "dir");
    } catch (error) {
      t.skip(`symlink creation unavailable: ${error.code}`);
      return;
    }
    assert.throws(
      () => writeOutputFile(linkedWorkspace.directory, `${OUTPUT_DIRECTORY}/review.json`, "{}"),
      /structured output directory is unavailable/,
    );
    linkedWorkspace.cleanup();
  } finally {
    current.cleanup();
    external.cleanup();
  }
});

test("sandbox rejects symlinks and junctions before realpath access", (t) => {
  const current = fixture();
  const external = scratchWorkspace();
  try {
    write(external.directory, "secret.txt", "secret");
    const link = path.join(current.directory, "root", "escape");
    try {
      fs.symlinkSync(external.directory, link, process.platform === "win32" ? "junction" : "dir");
    } catch (error) {
      t.skip(`symlink creation unavailable: ${error.code}`);
      return;
    }
    assert.throws(
      () => current.sandbox.readFile({ path: "root/escape/secret.txt" }),
      /symbolic links and junctions are not allowed/,
    );
    assert.doesNotMatch(current.sandbox.listFiles({ path: "root", recursive: true }), /secret\.txt/);
  } finally {
    current.cleanup();
    external.cleanup();
  }
});

test("sandbox rejects binary, invalid UTF-8, oversized, and wrong-type reads", () => {
  const current = fixture();
  try {
    write(current.directory, "root/binary.txt", Buffer.from([65, 0, 66]));
    write(current.directory, "root/invalid.txt", Buffer.from([0xc3, 0x28]));
    write(current.directory, "root/large.txt", Buffer.alloc(MAX_SOURCE_FILE_BYTES + 1, 65));
    fs.mkdirSync(path.join(current.directory, "root", "directory"));
    assert.throws(() => current.sandbox.readFile({ path: "root/binary.txt" }), /binary/);
    assert.throws(() => current.sandbox.readFile({ path: "root/invalid.txt" }), /UTF-8/);
    assert.throws(() => current.sandbox.readFile({ path: "root/large.txt" }), /byte limit/);
    assert.throws(() => current.sandbox.readFile({ path: "root/directory" }), /regular file/);
  } finally {
    current.cleanup();
  }
});

test("overlong lines are cut at a code point boundary instead of failing the read", () => {
  const current = fixture();
  try {
    // Each euro sign is three bytes, so the cut cannot land on the limit exactly.
    const long = "€".repeat(MAX_LINE_BYTES);
    write(current.directory, "root/line.txt", `first\n${long}\nlast\n`);
    const read = JSON.parse(current.sandbox.readFile({ path: "root/line.txt" }));
    assert.deepEqual(read.truncated_lines, [2]);
    const [first, second, last] = read.content.split("\n");
    assert.equal(first, "1: first");
    assert.equal(last, "3: last");
    const kept = second.slice("2: ".length);
    assert.equal(kept, "€".repeat(Math.floor(MAX_LINE_BYTES / 3)));
    assert.equal(kept.isWellFormed(), true);
    assert.equal(
      Object.hasOwn(JSON.parse(current.sandbox.readFile({ path: "root/a.txt" })), "truncated_lines"),
      false,
    );

    write(current.directory, "root/tail.txt", `${"x".repeat(MAX_LINE_BYTES * 2)}needle tail\n`);
    const searched = JSON.parse(current.sandbox.searchText({ path: "root/tail.txt", query: "needle" }));
    assert.deepEqual(searched.matches, [{
      path: "root/tail.txt", line: 1, column: MAX_LINE_BYTES * 2 + 1,
      text: "needle tail", text_truncated: true,
    }]);
  } finally {
    current.cleanup();
  }
});

test("list_files is deterministic, bounded by recursion, and requires an allowed root", () => {
  const current = fixture();
  try {
    let relative = "root/deep";
    for (let depth = 0; depth < MAX_RECURSION_DEPTH + 3; depth++) {
      write(current.directory, `${relative}/level-${depth}.txt`, `${depth}`);
      relative += `/d${depth}`;
    }
    const listed = JSON.parse(current.sandbox.listFiles({ path: "root", recursive: true }));
    assert.equal(listed.entries[0].path, "root/a.txt");
    assert.equal(listed.entries.some((entry) => entry.path.includes(`d${MAX_RECURSION_DEPTH}`)), false);
    assert.equal(listed.truncated, true);
    const searched = JSON.parse(current.sandbox.searchText({ path: "root", query: "absent" }));
    assert.equal(searched.truncated, true);
    assert.throws(
      () => current.sandbox.listFiles({ path: "root/a.txt" }),
      /directory/,
    );
    assert.throws(
      () => current.sandbox.listFiles({ path: "single.txt" }),
      /directory/,
    );
  } finally {
    current.cleanup();
  }
});

test("directory traversal stops at the aggregate entry budget", (t) => {
  const current = fixture();
  const originalOpen = fs.opendirSync;
  let reads = 0;
  try {
    t.mock.method(fs, "opendirSync", (directory) => {
      if (path.resolve(directory) !== path.resolve(current.directory, "root")) {
        return originalOpen(directory);
      }
      return {
        readSync() {
          reads++;
          return { name: `missing-${reads}` };
        },
        closeSync() {},
      };
    });
    const listed = JSON.parse(current.sandbox.listFiles({ path: "root", recursive: true }));
    assert.equal(listed.truncated, true);
    assert.equal(reads, MAX_WALK_ENTRIES + 1);
  } finally {
    current.cleanup();
  }
});

test("search_text is literal, bounded, skips binary files, and supports exact allowed files", () => {
  const current = fixture();
  try {
    write(current.directory, "root/binary.dat", Buffer.from([0, 1, 2]));
    const recursive = JSON.parse(current.sandbox.searchText({ path: "root", query: "needle" }));
    assert.deepEqual(recursive.matches.map((match) => [match.path, match.line]), [
      ["root/a.txt", 2],
      ["root/nested/b.txt", 1],
    ]);
    const exact = JSON.parse(current.sandbox.searchText({ path: "single.txt", query: "single" }));
    assert.equal(exact.matches.length, 1);
    assert.throws(
      () => current.sandbox.searchText({ path: "root", query: "" }),
      /invalid search_text arguments/,
    );
    assert.throws(
      () => current.sandbox.searchText({ path: "root", query: "x".repeat(257) }),
      /invalid search_text arguments/,
    );
  } finally {
    current.cleanup();
  }
});

test("tool argument objects reject inherited and unexpected properties", () => {
  const current = fixture();
  try {
    assert.throws(
      () => current.sandbox.readFile(Object.assign(Object.create({ inherited: true }), {
        path: "root/a.txt",
      })),
      /malformed tool arguments/,
    );
    assert.throws(
      () => current.sandbox.listFiles({ path: "root", extra: true }),
      /malformed tool arguments/,
    );
  } finally {
    current.cleanup();
  }
});

test("listing, searching, and encoded tool results enforce hard result bounds", () => {
  const current = fixture();
  try {
    for (let index = 0; index <= MAX_LIST_ENTRIES; index++) {
      write(current.directory, `root/many/file-${String(index).padStart(4, "0")}.txt`, "match\n");
    }
    const listed = JSON.parse(current.sandbox.listFiles({ path: "root/many" }));
    assert.equal(listed.entries.length, MAX_LIST_ENTRIES);
    assert.equal(listed.truncated, true);

    write(
      current.directory,
      "root/matches.txt",
      Array.from({ length: MAX_SEARCH_RESULTS + 1 }, (_, index) => `match ${index}`).join("\n"),
    );
    const searched = JSON.parse(current.sandbox.searchText({ path: "root/matches.txt", query: "match" }));
    assert.equal(searched.matches.length, MAX_SEARCH_RESULTS);
    assert.equal(searched.truncated, true);

    // Long matching lines stop the search at the serialized budget rather than failing it.
    const longLines = Math.ceil(MAX_TOOL_RESULT_BYTES / MAX_LINE_BYTES) + 4;
    write(current.directory, "root/long-matches/a.txt",
      Array.from({ length: longLines }, () => `match ${"x".repeat(MAX_LINE_BYTES)}`).join("\n"));
    const long = current.sandbox.searchText({ path: "root/long-matches", query: "match" });
    assert.ok(Buffer.byteLength(long, "utf8") <= MAX_TOOL_RESULT_BYTES);
    const parsed = JSON.parse(long);
    assert.equal(parsed.truncated, true);
    assert.ok(parsed.matches.length > 0 && parsed.matches.length < longLines, parsed.matches.length);
    assert.ok(parsed.matches.every((match) => match.text_truncated === true));

    assert.throws(
      () => boundJson({ content: "x".repeat(MAX_TOOL_RESULT_BYTES) }),
      /tool result exceeds byte limit/,
    );
  } finally {
    current.cleanup();
  }
});
