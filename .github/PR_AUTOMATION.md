# Pull request automation

UniGetUI's PR automation is **advisory and disabled for external AI processing and
PR publication by default**. Merging these files does not provision credentials,
labels, repository settings, or a provider account. No automatic approval, merge,
closure, release, or issue triage is implemented.

## Default behavior

`pr-automation.yml` runs deterministic analysis from a trusted workflow revision.
It writes an Actions summary and one-day snapshot artifacts, not PR comments,
checks, labels, or repository settings. It reports scopes, affected package
managers, size, GitHub's first-contributor association, and unknown risk without
calling a model. Existing manual labels are untouched.

The stages distinguish disabled, skipped, waiting, superseded, ready, failed and
completed states. Disabled AI is not a failure. Admission failures fail the
workflow visibly; a missing or invalid result is never a clean review.

The workflow handles PR lifecycle events, completion of applicable CI workflows,
and maintainer-authorized manual dispatch. Only PRs targeting the repository's
default branch are eligible. Drafts and closed PRs do not start AI work.

## Activation controls

These are deployment prerequisites, not instructions to provision them
automatically:

| Repository variable / secret | Default | Meaning |
| --- | --- | --- |
| `PR_AUTOMATION_MODE` | `report-only` | Set to `publish` only after approving label/comment/review mutations |
| `PR_AUTOMATION_AI_ENABLED` | `false` | Exact `true` enables the gated AI path |
| `PR_AUTOMATION_API_BASE_URL` | unset | Approved HTTPS OpenAI-compatible chat-completions endpoint |
| `PR_AUTOMATION_MODEL` | unset | Approved model identifier; no model is shipped as a default |
| `PR_AUTOMATION_API_KEY` (secret) | unset | Provider credential, available only to read-only model jobs |

Invalid settings fail visibly. A missing provider secret when AI is enabled fails
the model action rather than inventing a review.

Even with AI enabled, each PR needs `automation/ai-review`. The most recent grant
must be attributable to someone who currently has write/maintain/admin permission.
The label is never added by this automation. Manual dispatch requires write
permission and does not bypass the label, evidence, CI, or output-validation
gates. There is no force switch.

An AI preview uses `report-only` plus explicitly enabled AI and a maintainer-granted
label. It produces only workflow artifacts; previews do not consume the published
review count. They can still incur provider charges. Control endpoint/model
spend with approved provider-side quotas before enabling previews.

## CI identity and applicability

The trusted policy reads the actual `pull_request` branch/path filters from:

- `.NET Tests` (`dotnet-test.yml`).
- `CLI Headless E2E` (`cli-headless-e2e.yml`), including its entire matrix.
- `Translation Validation` (`translation-validation.yml`).
- `PR Automation Tests` (`pr-automation-tests.yml`).

Unknown filter syntax fails closed. All changed files, including previous names
on renames, are considered. GitHub's REST limit is 3,000 files; incomplete
enumeration or larger changes fail instead of accepting partial evidence.
GitHub's own path-filter limits can prevent a workflow from starting on a very
large PR: missing applicable CI remains waiting, never successful.

Docs-only changes need no filtered code workflow. Automation-only changes require
automation tests, not an unrelated Windows build. Scheduled/manual CodeQL and
manual Build and Release are not PR prerequisites.

Every applicable workflow must have a successful latest aggregate run/attempt
associated with the exact PR, source head and base SHA. Source and synthetic merge
SHAs are treated separately; a merge commit must have the expected parents.
Missing fork associations are retried through full-run metadata or bound to the
current PR's verified merge commit. Source-only runs without verifiable PR/base
metadata remain waiting rather than being authorized from a branch name.
The triggering CI event is considered even while the run-list API lags. The
writer rechecks current identity and latest CI before review publication. A
base-only movement invalidates old CI evidence.

The existing .NET workflow's advisory full-trim step is not a zero-warning gate.
Green Windows/Linux checks do not prove all six NativeAOT RIDs were exercised.
This feature does not weaken or change existing application CI.

## Review stages

When explicitly enabled and opted in, AI classification and review wait for
applicable CI rather than spending provider calls on an unready change:

1. Collect complete bounded diff, changed-line locations, PR context/discussion,
   open-PR titles, and a sanitized read-only head tree at fixed SHAs.
2. Classify risk and advisory legitimacy/overlap hints.
3. Add deterministic mandatory specialists to the classifier's suggestions.
4. Run selected specialists independently, at most three at once.
5. Have the general reviewer independently inspect the change and accept, refine
   or reject every validated candidate.
6. Validate all identities, schemas, locations, dispositions, provenance and
   rendered size again before any publication.

| Specialist | Deterministic coverage |
| --- | --- |
| `dotnet-aot` | C#/XAML/project/build changes, JSON metadata, trimming, interop, lifetimes |
| `package-engine` | Package-engine/managers, versions, feeds, CLI operations and downloads |
| `ui-localization` | Avalonia/Consolonia, themes, bindings, language assets and translation tools |
| `skeptical` | Trust-boundary/build/release changes and non-low classified risk |
| `code-compressor` | Changed code/scripts/workflows; concrete optional simplifications |

The general reviewer always runs after required specialists. Specialists cannot
see one another's results. Missing required results block the general result and
publication. Optional simplifications are distinguished from correctness defects.
No Rust, RDP, OpenSpecs corpus, or protocol-specific reviewer is included.

Reviews are GitHub `COMMENT` reviews with a commit-bound body, never `APPROVE` or
`REQUEST_CHANGES`. Findings include validated changed paths and optional added
lines in the body; this initial implementation does not post separate inline
comments. It escapes model text, derives specialist attribution from validated
candidate references and retains human review as authoritative.

Automatic AI analysis stops after three published distinct-head reviews, or when
the current head already has one. Deterministic triage continues. Failed,
duplicate and preview runs do not increment the count. A same-head base movement
does not grant an additional review. Classification is not independently rerun
after this cap; this intentionally bounds the initial adoption's provider use.

## Trust boundaries and evidence limits

The imported local Node 24 runtime uses the OpenAI JavaScript SDK's
`chat.completions` API, not Copilot agentic workflows or a CLI wrapper. See
`actions/openai-agent/UPSTREAM.md` for source revision, licenses, dependency
patches and reproducible bundle commands.
The action pins text files to LF so Windows checkout settings do not alter its
bundled artifact or build inputs.

The runtime only offers bounded `read_file`, `list_files` and `search_text`.
It cannot execute commands, write arbitrary files, invoke GitHub, access the
environment through tools, or fetch arbitrary URLs. Each stage allows 16 turns
(24 for the general reviewer), 64 tool calls (100 for general), a 20-minute
deadline, two request retries and two output-repair attempts. The approved
provider's own quotas must bound monetary spend; turn limits are not a currency
budget. Workflow jobs also have timeouts.

All actions, policy, schemas, prompts and methodology come from the same trusted
`github.workflow_sha`. The contributor checkout is inert evidence: it never
supplies executable actions, configuration, hooks or package scripts. Git fetches
use a fresh repository, no persisted credentials, no system/global Git config,
and no submodule execution. Trusted Git attributes prevent the PR from hiding
text through a custom diff attribute.

The diff limit is 4 MiB; PR/discussion context is limited to 4 MiB and 1,000
discussion entries; overlap context permits 500 open PRs. The text head-tree
limit is 64 MiB and 20,000 files. Overflow fails rather than truncates. Binary
context is excluded using Git-style NUL detection and recorded explicitly;
binary changes remain represented in the diff, not claimed as inspected source.
Nested contributor agent instructions, `.github` automation, dependency trees
and symlinks are removed from the contextual tree but their changes remain in
the authoritative diff. Runtime path/realpath restrictions provide another
boundary. The evidence and outputs stay in same-run, one-day artifacts.

PR titles, bodies, discussion, other PR titles, source and diff may be sent to the
approved provider only after activation. Prompts explicitly treat all of them as
untrusted data. No model stage has a GitHub write credential. Runtime logs contain
bounded diagnostics rather than prompts, source, responses or provider secrets.
Available usage/retry/repair diagnostics are stored per stage; missing usage is
not presented as zero.

## Publication, labels and recovery

Only the publication job has PR/issue write scopes. It runs only in `publish`
mode and uses a serialized non-canceling writer queue. GitHub's documented
`queue: max` allows up to 100 pending writers; the job revalidates live state.
The initial implementation serializes all writers repository-wide for simplicity.
Head/base checks before mutations and commit-bound review bodies limit races;
GitHub label/comment APIs have no atomic compare-and-swap on a PR head.

`pr-automation/labels.json` is a proposal and ownership allowlist, not a label
creation script. Before any mutation, all proposed labels must already exist.
The automation reconciles only its owned families and never repurposes existing
`translations`, manager, dependency, `needs-author-answer`, or release labels.
`needs-review` / `needs-author-action` identify the next human separately from
`automation-failed`. AI legitimacy/overlap hints never close PRs.

The writer stores a bot-owned state comment with a ten-minute publication lease.
It lists bot-authored, provenance-marked reviews to count distinct heads and
recover after a review succeeded but its response/state update was lost.
Unknown/corrupt state fails visibly. If a writer fails mid-publication, inspect
its run, wait for its lease to expire, and manually dispatch again. Do not delete
human comments or reset counters to hide errors.

Disable new model calls with `PR_AUTOMATION_AI_ENABLED=false`; disable new
publication with `PR_AUTOMATION_MODE=report-only`. Cancel in-flight workflows if
immediate cessation is needed. Disabling does not erase existing reviews, human
labels or state. Admission failures intentionally do not attempt privileged
recovery writes.

Before rollout, maintainers must verify Actions/fork/token policies, branch
protection and actual CI metadata. These could not all be read during discovery
(Actions policy: HTTP 403; branch protection: HTTP 404). Preserve DevOps-managed
CODEOWNERS and `.github/randy.yml`; specifically verify Randy's interpretation of
comment-only reviews before AI publication. Human review of `.github` remains
required. `GITHUB_TOKEN` mutations may not trigger downstream workflows; adopting
a bot/App identity is a separate reviewed deployment change.

## Shared methodology

Only `skeptical-reviewer` and `code-compressor` are explicitly mirrored from
`Devolutions/devolutions-gateway`; the six project-owned translation skills remain
local and untouched. Provenance is recorded alongside the mirrored skills.
Prompts, schemas, policy and runtime are not automatically synchronized.

`sync-skills.yml` is manual and read-only: it prepares a preview/diff artifact, not
a scheduled mutation or PR. Approving a Sunday 23:00 UTC schedule and a narrowly
scoped bot/App to open/update `automation/sync-shared-skills` is deferred deployment
work. No IronRDP release environment or bot credential is reused.

## Local validation

Use Node 24. Install the runtime dependencies before running the policy tests,
which also exercise the runtime's configuration/validator contract:

```powershell
# .github\actions\openai-agent
npm ci --ignore-scripts
npm run check
npm audit

# .github\pr-automation
npm ci --ignore-scripts
npm run check
npm audit

# .github\actions\sync-skills
node --test test/*.test.js
```

From the repository root, confirm the rebuilt bundle is unchanged:

```powershell
git diff --exit-code -- .github\actions\openai-agent\dist
git diff --check
```

`PR Automation Tests` runs policy/runtime/sync tests and bundle checks on Windows
and Ubuntu without provider credentials. Tests use mocked GitHub/provider APIs
and temporary Git/filesystem fixtures, not live PR mutations. Workflow YAML is
also parsed by configuration checks.

Actionlint v1.7.12 predates GitHub's `concurrency.queue` syntax. For that version
only, the following narrow compatibility exception is needed; the queue is
validated against GitHub's current workflow syntax documentation:

```powershell
go run github.com/rhysd/actionlint/cmd/actionlint@v1.7.12 -shellcheck= -pyflakes= -ignore 'unexpected key "queue" for "concurrency" section' .github\workflows\pr-automation.yml .github\workflows\review-pipeline.yml .github\workflows\pr-automation-tests.yml .github\workflows\sync-skills.yml
```

Application C# code is unchanged. No application test or NativeAOT RID coverage
is implied by these automation checks. Provider interoperability, actual hosted
workflow execution and Ubuntu behavior must be confirmed in the staged rollout,
not represented as verified by a Windows-only local run.
