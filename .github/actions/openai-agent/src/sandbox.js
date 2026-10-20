"use strict";

const fs = require("node:fs");
const path = require("node:path");
const { TextDecoder } = require("node:util");

const { ActionError, fail } = require("./errors");
const {
  MAX_LINE_BYTES, MAX_LIST_ENTRIES, MAX_PATH_BYTES, MAX_QUERY_BYTES, MAX_READ_LINES,
  MAX_RECURSION_DEPTH, MAX_SEARCH_FILES, MAX_SEARCH_RESULTS, MAX_SOURCE_FILE_BYTES,
  MAX_TOOL_RESULT_BYTES, MAX_WALK_ENTRIES,
} = require("./limits");

const decoder = new TextDecoder("utf-8", { fatal: true });
const OUTPUT_DIRECTORY = ".openai-agent-output";
const OUTPUT_FILE = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\.json$/;

function isInside(parent, child) {
  const relative = path.relative(parent, child);
  return relative === "" || (!relative.startsWith(`..${path.sep}`) && relative !== ".." &&
    !path.isAbsolute(relative));
}

function normalizeRepositoryPath(value) {
  if (typeof value !== "string" || value.length === 0 ||
      Buffer.byteLength(value, "utf8") > MAX_PATH_BYTES ||
      value.includes("\\") || /[\u0000-\u001F\u007F]/.test(value) ||
      value.startsWith("/") || /^[A-Za-z]:/.test(value)) {
    fail("invalid path");
  }
  const segments = value.split("/");
  if (segments.some((segment) => segment === "" || segment === "." || segment === ".." ||
      segment.toLowerCase() === ".git")) {
    fail("invalid path");
  }
  return segments.join("/");
}

// Models routinely spell a tool path as `./file`, `dir/`, or `.`. Those segments name nothing, so
// they are dropped before the strict check instead of costing the call. An absolute path passes
// through untouched, so it, `..`, and `.git` stay rejected. An empty result names the workspace root.
function normalizeToolPath(value) {
  if (typeof value !== "string" || value.startsWith("/")) return value;
  return value.split("/").filter((segment) => segment !== "" && segment !== ".").join("/");
}

// A line longer than the read limit is cut at a code point boundary rather than failing the whole
// read, so one long line, such as a pull request body, no longer hides the rest of its file.
function boundedLine(line, from = 0) {
  let bytes = 0;
  let end = from;
  for (const character of line.slice(from)) {
    const size = Buffer.byteLength(character, "utf8");
    if (bytes + size > MAX_LINE_BYTES) break;
    bytes += size;
    end += character.length;
  }
  return line.slice(from, end);
}

function validateOutputFilePath(value) {
  if (value === "") return "";
  const relative = normalizeRepositoryPath(value);
  const prefix = `${OUTPUT_DIRECTORY}/`;
  const name = relative.startsWith(prefix) ? relative.slice(prefix.length) : "";
  if (relative !== value || name.includes("/") || !OUTPUT_FILE.test(name)) {
    fail("invalid structured output file");
  }
  return relative;
}

function writeOutputFile(workspace, repositoryPath, content) {
  const relative = validateOutputFilePath(repositoryPath);
  if (relative === "") return "";
  const sandbox = new WorkspaceSandbox(workspace);
  const directory = path.join(sandbox.workspace, OUTPUT_DIRECTORY);
  try {
    fs.mkdirSync(directory, { mode: 0o700 });
  } catch (error) {
    if (error?.code !== "EEXIST") fail("structured output directory is unavailable");
  }
  try {
    const metadata = fs.lstatSync(directory);
    if (metadata.isSymbolicLink() || !metadata.isDirectory()) {
      fail("structured output directory is unavailable");
    }
  } catch (error) {
    if (error instanceof ActionError) throw error;
    fail("structured output directory is unavailable");
  }

  let descriptor;
  try {
    descriptor = fs.openSync(
      path.join(directory, path.basename(relative)),
      fs.constants.O_WRONLY | fs.constants.O_CREAT | fs.constants.O_EXCL |
        (fs.constants.O_NOFOLLOW || 0),
      0o600,
    );
    if (!fs.fstatSync(descriptor).isFile()) fail("structured output path is not a regular file");
    fs.writeFileSync(descriptor, content, "utf8");
    fs.fsyncSync(descriptor);
    return relative;
  } catch (error) {
    if (error instanceof ActionError) throw error;
    fail("structured output file is unavailable");
  } finally {
    if (descriptor !== undefined) fs.closeSync(descriptor);
  }
}

function decodeText(buffer) {
  if (buffer.includes(0)) fail("binary file is not readable");
  try {
    return decoder.decode(buffer);
  } catch {
    fail("file is not valid UTF-8 text");
  }
}

function boundJson(value) {
  const encoded = JSON.stringify(value);
  if (Buffer.byteLength(encoded, "utf8") > MAX_TOOL_RESULT_BYTES) {
    fail("tool result exceeds byte limit");
  }
  return encoded;
}

class WorkspaceSandbox {
  constructor(workspace, capabilities) {
    if (typeof workspace !== "string" || workspace.length === 0) fail("workspace is unavailable");
    let workspaceReal;
    try {
      workspaceReal = fs.realpathSync(workspace);
      if (!fs.statSync(workspaceReal).isDirectory()) fail("workspace is not a directory");
    } catch (error) {
      if (error instanceof ActionError) throw error;
      fail("workspace is unavailable");
    }
    this.workspace = workspaceReal;
    this.allowedRoots = [];
    this.allowedFiles = [];
    if (capabilities) {
      for (const entry of capabilities.allowedRoots) {
        const resolved = this.resolve(entry, "directory", false);
        this.allowedRoots.push(resolved);
      }
      for (const entry of capabilities.allowedFiles) {
        const resolved = this.resolve(entry, "file", false);
        this.allowedFiles.push(resolved);
      }
    }
  }

  resolve(repositoryPath, expectedType, requireCapability = true) {
    const relative = normalizeRepositoryPath(repositoryPath);
    const lexical = path.resolve(this.workspace, ...relative.split("/"));
    if (!isInside(this.workspace, lexical)) fail("path escapes workspace");

    let cursor = this.workspace;
    try {
      for (const segment of relative.split("/")) {
        cursor = path.join(cursor, segment);
        const metadata = fs.lstatSync(cursor);
        if (metadata.isSymbolicLink()) fail("symbolic links and junctions are not allowed");
      }
      const metadata = fs.lstatSync(lexical);
      if (expectedType === "file" && !metadata.isFile()) fail("path is not a regular file");
      if (expectedType === "directory" && !metadata.isDirectory()) fail("path is not a directory");
      if (!metadata.isFile() && !metadata.isDirectory()) fail("unsupported filesystem object");
      const real = fs.realpathSync(lexical);
      if (!isInside(this.workspace, real)) fail("path escapes workspace");
      const resolved = { lexical, real, relative };
      if (requireCapability && !this.isAllowed(resolved, expectedType)) {
        fail("path is outside allowed capabilities");
      }
      return resolved;
    } catch (error) {
      if (error instanceof ActionError) throw error;
      fail("path is unavailable");
    }
  }

  isAllowed(target, expectedType) {
    if (expectedType === "file" && this.allowedFiles.some((file) =>
      file.relative === target.relative && file.real === target.real)) {
      return true;
    }
    return this.allowedRoots.some((root) =>
      (target.relative === root.relative || target.relative.startsWith(`${root.relative}/`)) &&
      isInside(root.real, target.real));
  }

  readWorkflowFile(repositoryPath, maxBytes) {
    const target = this.resolve(repositoryPath, "file", false);
    return this.readText(target, maxBytes);
  }

  readText(target, maxBytes = MAX_SOURCE_FILE_BYTES) {
    let descriptor;
    try {
      const currentReal = fs.realpathSync(target.lexical);
      if (currentReal !== target.real || !isInside(this.workspace, currentReal)) {
        fail("path changed during access");
      }
      const noFollow = fs.constants.O_NOFOLLOW || 0;
      descriptor = fs.openSync(target.real, fs.constants.O_RDONLY | noFollow);
      const metadata = fs.fstatSync(descriptor);
      if (!metadata.isFile()) fail("path is not a regular file");
      if (metadata.size > maxBytes) fail("file exceeds byte limit");
      return decodeText(fs.readFileSync(descriptor));
    } catch (error) {
      if (error instanceof ActionError) throw error;
      fail("file is unavailable");
    } finally {
      if (descriptor !== undefined) fs.closeSync(descriptor);
    }
  }

  readFile(args) {
    assertObject(args, ["path", "start_line", "end_line"]);
    if (typeof args.path !== "string") fail("read_file path must be a string");
    const start = args.start_line === undefined ? 1 : positiveInteger(args.start_line, "invalid start line");
    const requestedEnd = args.end_line === undefined ? start + MAX_READ_LINES - 1 :
      positiveInteger(args.end_line, "invalid end line");
    if (requestedEnd < start || requestedEnd - start + 1 > MAX_READ_LINES) fail("invalid line range");

    const target = this.resolve(normalizeToolPath(args.path), "file");
    const lines = this.readText(target).split(/\r?\n/);
    if (start > lines.length) fail("start line exceeds file length");
    const end = Math.min(requestedEnd, lines.length);
    const selected = [];
    const truncatedLines = [];
    for (let index = start; index <= end; index++) {
      let line = lines[index - 1];
      if (Buffer.byteLength(line, "utf8") > MAX_LINE_BYTES) {
        line = boundedLine(line);
        truncatedLines.push(index);
      }
      selected.push(`${index}: ${line}`);
    }
    return boundJson({
      ok: true,
      path: target.relative,
      start_line: start,
      end_line: end,
      truncated: end < lines.length,
      ...(truncatedLines.length === 0 ? {} : { truncated_lines: truncatedLines }),
      content: selected.join("\n"),
    });
  }

  listFiles(args) {
    assertObject(args, ["path", "recursive"]);
    if (typeof args.path !== "string" ||
        (args.recursive !== undefined && typeof args.recursive !== "boolean")) {
      fail("invalid list_files arguments");
    }
    const requested = normalizeToolPath(args.path);
    // The workspace root is not itself a capability, so listing it names the ones that exist.
    if (requested === "") return this.listCapabilities();
    const target = this.resolve(requested, "directory");
    const entries = [];
    const traversal = this.walk(target, args.recursive === true, ({ relative, metadata }) => {
      if (entries.length >= MAX_LIST_ENTRIES) return false;
      entries.push({ path: relative, type: metadata.isDirectory() ? "directory" : "file" });
      return true;
    });
    return boundJson({
      ok: true,
      path: target.relative,
      recursive: args.recursive === true,
      truncated: traversal.truncated,
      entries,
    });
  }

  listCapabilities() {
    const entries = [
      ...this.allowedRoots.map((root) => ({ path: root.relative, type: "directory" })),
      ...this.allowedFiles.map((file) => ({ path: file.relative, type: "file" })),
    ].sort((left, right) => left.path.localeCompare(right.path, "en"));
    return boundJson({
      ok: true,
      path: ".",
      recursive: false,
      truncated: entries.length > MAX_LIST_ENTRIES,
      entries: entries.slice(0, MAX_LIST_ENTRIES),
    });
  }

  searchText(args) {
    assertObject(args, ["path", "query"]);
    if (typeof args.path !== "string" || typeof args.query !== "string" ||
        args.query.length === 0 || Buffer.byteLength(args.query, "utf8") > MAX_QUERY_BYTES ||
        /[\u0000-\u001F\u007F]/.test(args.query)) {
      fail("invalid search_text arguments");
    }

    const requested = normalizeToolPath(args.path);
    let target;
    try {
      target = this.resolve(requested, "file");
    } catch (fileError) {
      if (!(fileError instanceof ActionError) ||
          !["path is not a regular file", "unsupported filesystem object"].includes(fileError.code)) {
        throw fileError;
      }
      target = this.resolve(requested, "directory");
    }

    const results = [];
    let filesSearched = 0;
    let traversal = { truncated: false };
    // Matches are collected against the serialized budget, so enough long lines end the search with
    // what fits instead of failing the whole call. The envelope is reserved at its largest shape.
    let remainingBytes = MAX_TOOL_RESULT_BYTES - Buffer.byteLength(JSON.stringify({
      ok: true, path: target.relative, files_searched: MAX_SEARCH_FILES, truncated: false, matches: [],
    }), "utf8");
    let budgetExhausted = false;
    const collect = (match) => {
      const size = Buffer.byteLength(JSON.stringify(match), "utf8") + (results.length === 0 ? 0 : 1);
      if (size > remainingBytes) {
        budgetExhausted = true;
        return false;
      }
      remainingBytes -= size;
      results.push(match);
      return results.length < MAX_SEARCH_RESULTS;
    };
    const searchFile = (file) => {
      if (filesSearched >= MAX_SEARCH_FILES || results.length >= MAX_SEARCH_RESULTS ||
          budgetExhausted) return false;
      filesSearched++;
      let text;
      try {
        text = this.readText(file);
      } catch (error) {
        if (error instanceof ActionError &&
            ["binary file is not readable", "file is not valid UTF-8 text", "file exceeds byte limit"]
              .includes(error.code)) {
          return true;
        }
        throw error;
      }
      for (const [index, line] of text.split(/\r?\n/).entries()) {
        const column = line.indexOf(args.query);
        if (column === -1) continue;
        // An overlong line is shown from its first match, so the returned text contains it.
        const match = Buffer.byteLength(line, "utf8") <= MAX_LINE_BYTES
          ? { path: file.relative, line: index + 1, text: line }
          : {
            path: file.relative, line: index + 1, column: column + 1,
            text: boundedLine(line, column), text_truncated: true,
          };
        if (!collect(match)) return false;
      }
      return true;
    };

    if (fs.lstatSync(target.real).isFile()) {
      searchFile(target);
    } else {
      traversal = this.walk(target, true, ({ lexical, real, relative, metadata }) => {
        if (!metadata.isFile()) return true;
        return searchFile({ lexical, real, relative });
      });
    }
    return boundJson({
      ok: true,
      path: target.relative,
      files_searched: filesSearched,
      truncated: traversal.truncated || budgetExhausted ||
        filesSearched >= MAX_SEARCH_FILES || results.length >= MAX_SEARCH_RESULTS,
      matches: results,
    });
  }

  walk(root, recursive, visitor) {
    let remainingEntries = MAX_WALK_ENTRIES;
    let truncated = false;

    const visit = (directory, depth) => {
      if (remainingEntries === 0) {
        truncated = true;
        return true;
      }
      const directoryEntries = readDirectoryNames(directory.real, remainingEntries);
      remainingEntries -= directoryEntries.names.length;
      truncated ||= directoryEntries.truncated;
      const names = directoryEntries.names.sort((left, right) => left.localeCompare(right, "en"));
      for (const name of names) {
        if (name.toLowerCase() === ".git") continue;
        let relative;
        try {
          relative = normalizeRepositoryPath(`${directory.relative}/${name}`);
        } catch (error) {
          if (error instanceof ActionError) continue;
          throw error;
        }
        const lexical = path.join(directory.lexical, name);
        let metadata;
        try {
          metadata = fs.lstatSync(lexical);
        } catch {
          continue;
        }
        if (metadata.isSymbolicLink() || (!metadata.isFile() && !metadata.isDirectory())) continue;
        const real = fs.realpathSync(lexical);
        if (!isInside(root.real, real) || !isInside(this.workspace, real)) continue;
        const entry = { lexical, real, relative, metadata };
        if (visitor(entry) === false) {
          truncated = true;
          return false;
        }
        if (recursive && metadata.isDirectory()) {
          if (depth >= MAX_RECURSION_DEPTH || remainingEntries === 0) {
            truncated = true;
          } else if (visit({ lexical, real, relative }, depth + 1) === false) {
            return false;
          }
        }
      }
      return true;
    };
    visit(root, 1);
    return { truncated };
  }
}

function readDirectoryNames(directory, maximumEntries) {
  let handle;
  try {
    handle = fs.opendirSync(directory);
    const names = [];
    while (names.length < maximumEntries) {
      const entry = handle.readSync();
      if (entry === null) return { names, truncated: false };
      names.push(entry.name);
    }
    return { names, truncated: handle.readSync() !== null };
  } catch {
    fail("directory is unavailable");
  } finally {
    if (handle !== undefined) handle.closeSync();
  }
}

function assertObject(value, allowedKeys) {
  if (value === null || typeof value !== "object" || Array.isArray(value) ||
      Object.getPrototypeOf(value) !== Object.prototype ||
      Object.keys(value).some((key) => !allowedKeys.includes(key))) {
    fail("malformed tool arguments");
  }
}

function positiveInteger(value, code) {
  if (!Number.isSafeInteger(value) || value < 1) fail(code);
  return value;
}

module.exports = {
  OUTPUT_DIRECTORY, WorkspaceSandbox, boundJson, normalizeRepositoryPath, normalizeToolPath,
  validateOutputFilePath, writeOutputFile,
};
