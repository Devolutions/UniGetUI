"use strict";

const labels = require("../labels.json");
const { analyze } = require("../policy");
const HEAD = "a".repeat(40), BASE = "b".repeat(40), AUTOMATION = "c".repeat(40), MERGE = "d".repeat(40);
const repo = { owner: "Devolutions", repo: "UniGetUI" };
const file = (filename = "docs/example.md", additions = 1, deletions = 0) =>
  ({ filename, status: "modified", additions, deletions });
const pr = (overrides = {}) => ({
  number: 42, state: "open", draft: false, changed_files: 1, title: "Change a document", body: "A narrow fix",
  head: { sha: HEAD }, base: { sha: BASE, ref: "main", repo: { full_name: "Devolutions/UniGetUI" } },
  author_association: "CONTRIBUTOR", labels: [], ...overrides,
});
const run = (overrides = {}) => ({
  id: 1, run_attempt: 1, path: ".github/workflows/dotnet-test.yml", event: "pull_request",
  head_sha: HEAD, repository: { full_name: "Devolutions/UniGetUI" },
  pull_requests: [{ number: 42, head: { sha: HEAD }, base: { sha: BASE } }],
  status: "completed", conclusion: "success", ...overrides,
});
function snapshot(files = [file()]) {
  return {
    version: 1, number: 42, head: HEAD, base: BASE, automationSha: AUTOMATION,
    runId: "123", baseRef: "main", files, analysis: analyze(pr({ changed_files: files.length }), files),
    status: "ready",
  };
}
function report(input = snapshot()) {
  const context = { number: input.number, head: HEAD, base: BASE, automationSha: AUTOMATION,
    files: input.files.map((entry) => ({ ...entry, addedLines: [1] })), overlap: [] };
  return {
    version: 1, number: input.number, head: HEAD, base: BASE, automationSha: AUTOMATION, runId: "123", context,
    classifier: { head: HEAD, risk: "low", legitimacy: false, overlap: [], reviewers: [], summary: "Isolated documentation." },
    specialists: [],
    review: { head: HEAD, reviewer: "general", summary: "No correctness issues found.", findings: [], dispositions: [] },
  };
}
function finding(overrides = {}) {
  return { id: "a-finding", path: "docs/example.md", line: 1, severity: "medium",
    title: "A concrete issue", body: "When this case occurs, behavior is incorrect.", sources: [], ...overrides };
}
const env = {
  AUTOMATION_MODE: "publish", AUTOMATION_AI_ENABLED: "true", AUTOMATION_MODEL: "approved-model",
  AUTOMATION_API_BASE_URL: "https://example.invalid/v1", AUTOMATION_SHA: AUTOMATION, GITHUB_RUN_ID: "123",
};

function fake(options = {}) {
  const state = {
    pr: pr({ labels: [{ name: "automation/ai-review" }] }), files: [file()], runs: [], reviews: [],
    comments: [], writes: [], optInEvents: [{ id: 1, event: "labeled", label: { name: "automation/ai-review" },
      actor: { login: "maintainer" } }], permission: "write", labels, ...options,
  };
  const rest = { pulls: {}, repos: {}, issues: {}, actions: {} };
  const methods = {};
  function add(group, name, fn) {
    const key = `${group}.${name}`;
    rest[group][name] = async (args) => ({ data: await fn(args) });
    methods[key] = rest[group][name];
  }
  add("pulls", "get", () => state.pr);
  add("repos", "get", () => ({ default_branch: "main" }));
  add("repos", "getCollaboratorPermissionLevel", () => ({ permission: state.permission }));
  add("repos", "getCommit", () => ({ parents: [{ sha: HEAD }, { sha: BASE }] }));
  add("repos", "listPullRequestsAssociatedWithCommit", () => [state.pr]);
  add("pulls", "listFiles", () => state.files);
  add("pulls", "listReviews", () => state.reviews);
  add("pulls", "list", () => []);
  add("pulls", "listReviewComments", () => []);
  add("issues", "listEvents", () => state.optInEvents);
  add("issues", "listComments", () => state.comments);
  add("issues", "listLabelsForRepo", () => state.labels);
  add("actions", "listWorkflowRuns", (args) => state.runs.filter((entry) => entry.head_sha === args.head_sha &&
    entry.path === `.github/workflows/${args.workflow_id}`));
  add("actions", "getWorkflowRun", (args) => state.runs.find((entry) => entry.id === args.run_id));
  add("issues", "addLabels", (args) => {
    state.writes.push(["labels", args]);
    state.pr.labels.push(...args.labels.map((name) => ({ name })));
    return state.pr.labels;
  });
  add("issues", "removeLabel", (args) => {
    state.writes.push(["remove", args]);
    state.pr.labels = state.pr.labels.filter((label) => label.name !== args.name);
    return [];
  });
  add("issues", "createComment", (args) => {
    const comment = { id: state.comments.length + 1, body: args.body, user: { login: "github-actions[bot]", type: "Bot" } };
    state.comments.push(comment); state.writes.push(["comment", args]); return comment;
  });
  add("issues", "updateComment", (args) => {
    const comment = state.comments.find((entry) => entry.id === args.comment_id);
    comment.body = args.body; state.writes.push(["update", args]); return comment;
  });
  add("pulls", "createReview", (args) => {
    const review = { id: state.reviews.length + 1, body: args.body, state: "COMMENTED", commit_id: args.commit_id,
      user: { login: "github-actions[bot]", type: "Bot" } };
    state.reviews.push(review); state.writes.push(["review", args]); return review;
  });
  return { state, github: {
    rest,
    paginate: async (method, args) => {
      const data = (await method(args)).data;
      if (!Array.isArray(data)) throw new Error("Unexpected mocked pagination");
      return data;
    },
  }, methods };
}

module.exports = { HEAD, BASE, AUTOMATION, MERGE, repo, file, pr, run, snapshot, report, finding, env, fake };
