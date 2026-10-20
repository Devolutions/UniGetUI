# UniGetUI Issue Intelligence & Automatic Triage

This directory (`.github/issue-automation/`) and `.github/workflows/issue-automation.yml` provide deterministic and optional AI-assisted issue triage, routing-label publication, and maintainer backlog digests tailored to `Devolutions/UniGetUI`.

## 1. Rollout Modes & Conservative Defaults

All rollout controls default to safe, read-only behavior when repository variables are unset:

| Variable / Secret | Default | Description |
|---|---|---|
| `vars.ISSUE_AUTOMATION_MODE` | `report-only` | `report-only` (job summaries + artifacts only), `shadow` (additive labels only on issues with `automation/ai-triage`), or `active` (additive routing labels on open issues). |
| `vars.ISSUE_AUTOMATION_AI_ENABLED` | `false` | Enables optional bounded AI triage (`triage` + conditional `verifier`) via `.github/actions/openai-agent`. |
| `vars.ISSUE_AUTOMATION_DAILY_BUDGET` | `0` | Maximum daily AI issue evaluations (`1..100` when AI is enabled). |
| `vars.ISSUE_AUTOMATION_API_BASE_URL` | `""` | Explicit HTTPS OpenAI-compatible API endpoint required when AI is enabled. |
| `vars.ISSUE_AUTOMATION_MODEL` | `""` | Model identifier required when AI is enabled. |
| `secrets.ISSUE_AUTOMATION_API_KEY` | unset | API credential passed only to the read-only `ai-triage` job. |

## 2. Label Ownership & Human-Override Invariants

Labels in `.github/issue-automation/labels.json` are partitioned into three strict ownership classes:

### Automation-Owned Additive Routing Labels (`automation-additive`)
- **Type:** `bug`, `new-feature`, `enhancement` (proposed only when no type label is already present)
- **Package Managers:** `winget`, `scoop`, `chocolatey`, `pip`, `npm`, `cargo`, `vcpkg`, `dotnet-tool`, `powershell`, `msstore`, `apt`, `dnf`, `pacman`, `flatpak`, `snap`, `homebrew`
- **Platforms:** `platform/windows`, `platform/linux`, `platform/macos`, `platform/arm64`
- **Areas:** `area/ui-avalonia`, `area/tui`, `area/cli-daemon`, `area/bundles`, `area/updates-scheduler`, `area/elevation-security`, `area/localization`, `area/installer-packaging`

### Protected Maintainer Workflow & Judgment Labels (`human-protected`)
- `ready-to-go`, `needs-further-study`, `needs-author-answer`, `waiting-for-user-feedback`, `ready-for-release`, `released`, `waiting-for-something`, `important`, `hard-one`, `duplicate`, `not-UniGetUI`
- Automation **never** adds, removes, or overwrites protected labels.
- If a non-bot user removes any automation-owned label (`unlabeled` event), `humanRemovedLabels()` records that override and automation will not re-add it.
- `publish-labels` is additive-only (`POST /repos/{owner}/{repo}/issues/{number}/labels`), verifies that the live `contentFingerprint` still matches the analyzed snapshot, and never closes issues or posts unsolicited comments on user threads.

## 3. Deterministic Evidence & Maintainer Queue Digests

Even with `ISSUE_AUTOMATION_AI_ENABLED=false`, `policy.js` and `evidence.js` compute:
- **Version era & conflict detection:** Distinguishes `2026.x` Avalonia, `3.3.6–3.3.7` transition, and legacy `<=3.3.5` reports; preserves malformed version strings (e.g. `3.11beta1`, `3.1.2-beta0`) and flags form-vs-log mismatches.
- **Startup-noise filtering:** Filters routine `"which"` binary discovery and installed-package count lines from `UniGetUI Log` so unrelated bugs are not mislabeled with every active package manager.
- **Command-equivalence & backend detection:** Identifies WinGet COM API vs bundled `winget-cli_x64` (`Pinget`) vs system CLI, and flags non-equivalent terminal comparisons (`--source`, `--scope`, or UniGetUI-injected `--location` arguments).
- **Release-pinned capability matching:** Matches longstanding feature requests against `.github/issue-automation/capabilities.json` (`v2026.3.2`) while explicitly preserving unmet or distinct scope.
- **Maintainer digest queues:** Groups backlog issues into:
  1. `current-regressions` (including candidate open fix PR links)
  2. `author-replied` (`waiting-for-user-feedback` / `needs-author-answer` where the author responded after the latest maintainer comment)
  3. `release-verification` (`released` / `ready-for-release` issues still open, flagging unresolved post-release follow-up)
  4. `implemented-or-partial`
  5. `related-clusters`
  6. `information-gaps`
  7. `upstream-or-environment`
  8. `roadmap-requests`
