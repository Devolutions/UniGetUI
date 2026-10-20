"use strict";

const { policy, SHA } = require("./policy");

function identity(pr) {
  if (!Number.isSafeInteger(pr.number) || pr.number <= 0 || !SHA.test(pr.head?.sha) || !SHA.test(pr.base?.sha)) {
    throw new Error("Invalid pull request identity");
  }
  return { number: pr.number, head: pr.head.sha, base: pr.base.sha };
}

function sameIdentity(pr, expected) {
  return pr.state === "open" && !pr.draft && pr.number === expected.number &&
    pr.head.sha === expected.head && pr.base.sha === expected.base;
}

async function permission(github, repo, username) {
  const { data } = await github.rest.repos.getCollaboratorPermissionLevel({ ...repo, username });
  return ["admin", "maintain", "write"].includes(data.permission);
}

async function optIn(github, repo, pr) {
  if (!pr.labels.some((label) => label.name === policy.optInLabel)) return false;
  const events = await github.paginate(github.rest.issues.listEvents, { ...repo, issue_number: pr.number, per_page: 100 });
  const event = events.filter((entry) => entry.event === "labeled" && entry.label?.name === policy.optInLabel)
    .sort((a, b) => b.id - a.id)[0];
  return !!event?.actor?.login && permission(github, repo, event.actor.login);
}

async function changedFiles(github, repo, pr) {
  if (pr.changed_files > policy.maxFiles) throw new Error("Changed-file evidence exceeds GitHub enumeration limit");
  const files = await github.paginate(github.rest.pulls.listFiles, { ...repo, pull_number: pr.number, per_page: 100 });
  if (files.length !== pr.changed_files) throw new Error("Changed-file enumeration incomplete");
  return files.map(({ filename, previous_filename, status, additions, deletions }) =>
    ({ filename, ...(previous_filename ? { previous_filename } : {}), status, additions, deletions }));
}

async function checkCi(github, repo, pr, required, observedRun) {
  const expected = identity(pr);
  const generations = [];
  for (const workflow of required) {
    const runs = [];
    // GitHub can expose the source commit or synthetic merge commit as a run's SHA.
    for (const sha of new Set([expected.head, pr.merge_commit_sha].filter((value) => SHA.test(value)))) {
      const listed = await github.paginate(github.rest.actions.listWorkflowRuns, {
        ...repo, workflow_id: workflow.file, event: "pull_request", per_page: 100, head_sha: sha,
      });
      if (listed.length >= 1000) throw new Error("CI run enumeration reached the API limit");
      runs.push(...listed);
    }
    // The triggering event can arrive before the run-list API reflects that generation.
    if (observedRun?.path === `.github/workflows/${workflow.file}`) runs.push(observedRun);
    const candidates = [];
    const ambiguous = [];
    const unique = new Map(runs.map((run) => [`${run.id}:${run.run_attempt}`, run]));
    for (let run of unique.values()) {
      if (run.path !== `.github/workflows/${workflow.file}` || run.event !== "pull_request" ||
          run.repository?.full_name !== `${repo.owner}/${repo.repo}`) continue;
      if (!run.pull_requests?.length) {
        // Listings and fork payloads may omit PR metadata; ask for the full run first.
        run = (await github.rest.actions.getWorkflowRun({ ...repo, run_id: run.id })).data;
      }
      if (run.path !== `.github/workflows/${workflow.file}` || run.event !== "pull_request" ||
          run.repository?.full_name !== `${repo.owner}/${repo.repo}`) continue;
      if (run.pull_requests?.some((entry) => entry.number === pr.number &&
          entry.head?.sha === expected.head && entry.base?.sha === expected.base)) candidates.push(run);
      else if (!run.pull_requests?.length) {
        if (SHA.test(pr.merge_commit_sha) && run.head_sha === pr.merge_commit_sha && run.head_sha !== expected.head) {
          // A current PR merge SHA with verified parents binds a fork run without relying on its branch name.
          candidates.push(run);
        } else if (run.head_sha === expected.head) ambiguous.push(run);
      }
    }
    const newer = (a, b) => b.id - a.id || b.run_attempt - a.run_attempt;
    const latest = candidates.sort(newer)[0];
    if (ambiguous.some((run) => !latest || newer(run, latest) < 0)) {
      return { ok: false, reason: `waiting for verifiable PR/base metadata for ${workflow.name}`, generations };
    }
    if (!latest) return { ok: false, reason: `waiting for current-head/base ${workflow.name}`, generations };
    if (latest.head_sha !== expected.head) {
      const merge = (await github.rest.repos.getCommit({ ...repo, ref: latest.head_sha })).data;
      if (merge.parents?.length !== 2 || !merge.parents.some((entry) => entry.sha === expected.head) ||
          !merge.parents.some((entry) => entry.sha === expected.base)) {
        return { ok: false, reason: `${workflow.name} has an unverifiable merge generation`, generations };
      }
    }
    // A workflow's aggregate conclusion includes its matrix jobs and cannot be replaced by an individual green leg.
    if (latest.status !== "completed" || latest.conclusion !== "success") {
      return { ok: false, reason: `${workflow.name} is not successful`, generations };
    }
    generations.push({ workflow: workflow.file, run: latest.id, attempt: latest.run_attempt, sha: latest.head_sha });
  }
  return { ok: true, generations };
}

async function currentPr(github, repo, number) {
  return (await github.rest.pulls.get({ ...repo, pull_number: number })).data;
}

async function resolve(github, context) {
  const repo = context.repo;
  if (context.eventName === "workflow_dispatch") {
    if (!await permission(github, repo, context.actor)) throw new Error("Manual dispatch requires repository write permission");
    const number = Number(context.payload.inputs?.["pr-number"]);
    if (!Number.isSafeInteger(number) || number <= 0) throw new Error("Invalid PR number");
    return [number];
  }
  if (context.eventName === "pull_request_target") return [context.payload.pull_request.number];
  if (context.eventName !== "workflow_run") throw new Error("Unsupported automation event");
  const run = context.payload.workflow_run;
  if (run.event !== "pull_request" || run.repository?.full_name !== `${repo.owner}/${repo.repo}`) return [];
  const numbers = new Set((run.pull_requests || []).map((entry) => entry.number));
  // Fork workflow_run payloads may omit pull_requests. Use commit association, never branch-name matching.
  if (numbers.size === 0 && SHA.test(run.head_sha)) {
    const prs = await github.paginate(github.rest.repos.listPullRequestsAssociatedWithCommit, {
      ...repo, commit_sha: run.head_sha, per_page: 100,
    });
    for (const pr of prs) if (pr.state === "open") numbers.add(pr.number);
    if (numbers.size === 0) {
      const open = await github.paginate(github.rest.pulls.list, { ...repo, state: "open", per_page: 100 });
      if (open.length > 500) throw new Error("PR resolution exceeds the bounded open-PR limit");
      for (const pr of open) {
        if (pr.head.sha === run.head_sha || pr.merge_commit_sha === run.head_sha) numbers.add(pr.number);
      }
    }
  }
  return [...numbers];
}

module.exports = { identity, sameIdentity, permission, optIn, changedFiles, checkCi, currentPr, resolve };
