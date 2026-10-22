---
name: fix-issue
description: Run the issue factory on a UniGetUI GitHub issue or a pasted bug description - triage, fix in a dedicated worktree, independent review, a PR, then Copilot review rounds. Use only when the user explicitly invokes /fix-issue with an issue number, an issue URL, or text describing a bug.
disable-model-invocation: true
---

# fix-issue

The user invoking this skill is their opt-in to the multi-agent issue factory.

The workflow (`.claude/workflows/issue-factory.js`) holds the control flow, limits and run data. Each role's rules live in its own agent file in `.claude/agents/`: `factory-triage`, `factory-builder`, `factory-reviewer`, `factory-shipper`, `factory-copilot-requester` and `factory-cleanup`. CI is not watched: it runs on GitHub, and the maintainer handles it. Change a role's behaviour in its agent file; change limits or ordering in the workflow.

1. Take the arguments as they are: an issue number (`5530`, `#5530`), an issue URL, or a chunk of text describing a bug. If there are none, ask for one and stop.
   Pass bug text through verbatim - do not summarise, rewrite or add to it; the workflow detects which kind of input it got.
2. Call the Workflow tool with:
   - `name`: `issue-factory` (defined in `.claude/workflows/issue-factory.js`)
   - `args`: the arguments as a plain string
3. Tell the user it is running and that they can follow it in /workflows. Do not poll.
4. When the workflow completes, first send one Claude app notification with the PushNotification tool (load it with ToolSearch if it is deferred), status `proactive`.
   One line, under 200 characters, no markdown, leading with what to act on. Examples:
   - `#5530 PR ready: github.com/Devolutions/UniGetUI/pull/5601 - Copilot clean, CI still running`
   - `#5530 PR open, 2 review findings unresolved: github.com/Devolutions/UniGetUI/pull/5601`
   - `#5530 stopped, no PR: builder blocked - worktree kept`
   - `#5530 not taken: needs author answers`
   For a pasted report use a few words of its title instead of the number. Send it even if the tool might skip it; a "not sent" result is fine.
5. Then report in the chat from its return value:
   - `pr_opened`: the PR link, the root cause in two sentences, and any non-blocking review notes. If `approved` is false, lead with that: the builder/reviewer loop ran out and the open findings are listed in the PR. Then the Copilot part, from `copilotOutcome` and `copilotRounds`:
     - `clean`: Copilot had no comments
     - `round_limit`: all 3 Copilot rounds were used; the last round's fixes are pushed but Copilot has not re-reviewed them
     - `all_declined`: the builder disproved every remaining Copilot comment and replied in-thread
     - `not_requested` / `timed_out`: Copilot could not be requested or never answered; say so, the PR is otherwise done
     - CI: the factory does not wait for it. Give the `checks` link and say CI may still be running; if it fails, remind the user that the TUI tests are known to flake and a re-run often clears it
   - `pr_opened_followup_incomplete`: a Copilot round's fix was not pushed, or its comments were not all answered and resolved (see `missing` and `shipperNotes`). The workflow stopped instead of requesting another review; lead with what is missing and where the unpushed commit is
   - `pr_opened_followup_blocked`: the PR is open, but the builder could not fix a Copilot round; its unfinished change is uncommitted in the worktree
   - `skipped` (triage): why the issue or report was not taken
   - For a pasted report, `issue` is null; name the branch (`fix/<slug>`) and any `triage.related_issue` it found
   - `not_a_bug` / `blocked`: what stopped it, and that the work-in-progress is in the returned worktree on the returned branch
   - Cleanup: the workflow deletes its worktree at the end. If `worktree` is still set, it was kept on purpose because it holds unpushed work; say so and give `cleanup.kept_reason`
   Check the PR or diff yourself before calling it done; the return value is the agents' claim, not proof.
