---
name: github-issue-fix
description: Investigate a GitHub issue in the UniGetUI repo and carry it through to a verified fix sitting in the working tree. Use this whenever the user points you at an issue in any form - a github.com/Devolutions/UniGetUI/issues/N link, "issue 5430", "[BUG] something is broken", or just "can you take a look at this" next to an issue reference - and any time they ask you to reproduce, triage, diagnose, root-cause, or fix a reported bug. Covers pulling the issue with gh, finding the real mechanism behind the symptom instead of the first plausible one, building with the right platform flags, and clearing both dotnet format gates before handing the work back.
---

# github issue fix

Use this when the user hands you an issue and wants it worked, not merely summarized. "Take a look at this issue" in this repo means investigate it, find the cause, and fix it - stopping to ask first wastes a round-trip they did not want.

The goal is a fix that builds clean and a diagnosis precise enough to survive a reviewer asking "why?".

## 1. Read the whole issue

```bash
gh issue view <N> --json title,body,state,labels,author,createdAt,comments
```

UniGetUI issues follow a template: app version, OS, description, repro steps, logs, and a "Relevant information" field that often links related issues. Read the linked ones too - they frequently contain the earlier fix that this issue is a follow-up to, or the PR that introduced the regression.

Pay attention to what the reporter says **works**. It is the most useful sentence in the whole report and it is easy to skim past. "Other settings implemented using CheckboxCard are announced correctly" hands you a working reference implementation for free.

## 2. Find the implicated code

Grep for the concrete nouns in the report: the control name, the setting key, the user-visible string, the error code. Issue authors here are usually technical and name real types.

If the issue quotes a user-facing string, remember it is a translation key - search the English source, then find who passes it.

## 3. Diagnose the real mechanism

Two habits do most of the work:

**Compare against the working sibling.** This codebase is full of near-duplicate implementations - parallel settings cards, per-manager classes, per-platform window code. When a report says "X is broken but Y works", pull up Y and diff it against X line by line. Drifted copies are the single most common bug shape here: someone fixed Y and never touched X. The fix is usually one missing call.

**Do not stop at the proximate cause.** When you find the missing line, keep asking why nothing else caught it. If a fallback path exists, work out why it did not fire. A diagnosis like "the name was not set" is worth much less than "the name was not set, *and* the card-level fallback could not supply it either because it only unwraps TextBlock and ContentControl while both cards use a StackPanel header". The second version tells you whether the fix is complete and gives you the commit message.

Check the premise before you build on it. Sometimes the reported behaviour is environmental, already fixed, or the reporter has misread what they saw. If the premise does not hold, say so plainly rather than inventing a fix for a bug that is not there.

For package-manager issues, get ground truth from the manager's real CLI output rather than reasoning about what it probably prints. Parsing bugs are almost always about output you have not actually looked at.

## 4. Fix it

Stay inside the reported scope. If you notice an adjacent control or manager with a related gap, mention it in your report and leave it alone - widening the diff is the user's call, not yours.

When the bug is drift between near-duplicates, prefer hoisting the shared behaviour into the common base over patching the copy, so the two cannot diverge again. But keep the resulting behaviour identical to the working sibling. Do not "improve" the already-working one in the same change: it inflates the diff and makes the fix harder to review and to revert.

Move existing explanatory comments along with code you relocate. Write no new ones - rationale belongs in the commit message.

## 5. Verify

Both gates must pass. A passing build is not enough: CI runs the formatters and fails the PR on any style diff.

```bash
# from src/
dotnet build <Project>/<Project>.csproj -p:Platform=x64 -p:UseSharedCompilation=false -v q --nologo
```

`-p:Platform=x64` matters - the app runs out of `bin/x64`. `-p:UseSharedCompilation=false` avoids leftover MSBuild node processes holding locks on obj files. If a build fails on a locked DLL, kill stray `dotnet` nodes and retry. Quit the app from the tray first if it is running, or the DLL stays stale.

```bash
dotnet format whitespace <proj> --include <changed-dir>/ --verify-no-changes; echo "exit=$?"
dotnet format style      <proj> --include <changed-dir>/ --verify-no-changes; echo "exit=$?"
```

Check the exit codes explicitly - these print little on success and `--verify-no-changes` signals failure through exit 2, which is easy to miss when you only skim the tail of the output. Run **both**; they catch different things.

Compare the warning count against a pre-change build so you can honestly say the remaining warnings are pre-existing.

Do not take screenshots of the app to prove a UI fix - the user verifies visuals themselves.

## 6. Review your own fix before you hand it back

Both gates passing only means the code compiles and is formatted. Before reporting, run the `self-review-change` skill over the diff: attack the diagnosis, check what the deleted code used to handle, and verify against the reporter's dependency version rather than the one on this box. Fold its findings into the report below, corrections included.

## 7. Report and stop

Leave the change in the working tree, uncommitted, and hand back:

- **Root cause** - the actual mechanism, in a couple of sentences
- **Fix** - what changed and why that shape, with `file:line` references
- **Verification** - build result and both format exit codes, stated plainly
- **Anything deliberately left out** - adjacent gaps, things you could not verify (a screen reader, a specific OS), scope you chose not to widen
- Offer to branch and commit

Stop there. Committing is a separate yes.

## If they say yes to committing

Check the branch immediately before committing - it changes between turns in this repo, and a fix has landed on `main` by mistake before:

```bash
git branch --show-current
```

If on `main`, branch first. Commit message: a short imperative subject, then the root cause and the reasoning that the code no longer carries as comments, then `Fixes #<N>`, then the attribution line the session's system reminder specifies.

Never push, and never open the PR, unless asked. Permission to commit is not permission to push.
