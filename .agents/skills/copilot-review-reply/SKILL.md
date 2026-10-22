---
name: copilot-review-reply
description: Read the automated review left on a pull request - GitHub Copilot, copilot-pull-request-reviewer, or any review bot - verify each finding against the real code, fix what genuinely holds, and answer the bot in-thread. Use this whenever the user mentions a bot review in any form: "copilot left a comment", "what did copilot say", "look at the review on my PR", "address the bot feedback", "reply to copilot", or just "did the review find anything". Covers finding every comment surface on the PR, separating findings that are right from ones that only sound right, deciding amend versus new commit, and posting a threaded reply through the gh API.
---

# copilot review reply

Automated reviewers are useful and often right about *what* is wrong while being wrong about *why*. Both halves matter: agreeing with a bad mechanism puts a wrong explanation in the permanent record of the PR, and dismissing a finding because its explanation is shaky loses a real bug.

Your job is to check each claim against the code, act on what survives, and answer plainly.

## 1. Collect every comment surface

Findings hide in three different places and it is easy to read one and miss the others.

```bash
# find the PR if you only have the branch
gh pr list --head <branch> --json number,title,url,state

# inline review comments - where Copilot puts the actual findings
gh api repos/Devolutions/UniGetUI/pulls/<N>/comments \
  --jq '.[] | "\(.id) | \(.user.login) | \(.path):\(.line) | in_reply_to=\(.in_reply_to_id)\n\(.body)\n"'

# review bodies - Copilot's overview, severity, and the findings index
gh api repos/Devolutions/UniGetUI/pulls/<N>/reviews \
  --jq '.[] | "--- \(.user.login) \(.state)\n\(.body)"'

# top-level conversation comments
gh pr view <N> --json comments --jq '.comments[] | "--- \(.author.login)\n\(.body)"'
```

Read the inline body carefully for sentences like "This issue also appears on line 72 of the same file." One comment routinely covers several sites, and fixing only the line it is anchored to leaves the review half-addressed.

Keep the comment `id` - you need it to reply in-thread. It is also the number in the `#discussion_r<ID>` anchor from the overview.

## 2. Verify each claim against the code

Open the file and trace the actual order of operations. Do not reason from the comment's summary of the code; comments are generated from a diff and the bot cannot always see initialization order, guards, or callers.

Findings sort into four buckets, and which one you are in determines the whole response:

- **Right about both outcome and mechanism.** Fix it, say so, move on.
- **Right about the outcome, wrong about the mechanism.** Fix it *and* correct the mechanism in the reply. This is the most common case and the one worth slowing down for. A claim that some value gets "overwritten" collapses if a null guard means it was never written in the first place - the end state may still be wrong, but for an entirely different reason.
- **Wrong.** Explain why, and point at the specific code that disproves it. Do not fix something that is not broken to make a bot happy.
- **Real, but pre-existing.** The same shape lives in code your diff never touched. Say so explicitly, then make a scope call: fix it here because the PR is already about that behaviour, or leave it. Either is defensible; silently absorbing unrelated work into the PR is not.

The things that most often invalidate a plausible-sounding finding: a null or empty guard, an early return, the order XAML assigns properties, a caller that cannot reach that state, or platform-specific behaviour that makes the concern moot on the platform in question.

## 3. Fix what holds

Keep symmetric code symmetric. If the PR's whole point was unifying two near-duplicate implementations, apply the follow-up fix to both - fixing one reintroduces exactly the drift the PR set out to remove.

Then run the same gates as any other change, from `src/`:

```bash
dotnet build <proj> -p:Platform=x64 -p:UseSharedCompilation=false -v q --nologo
dotnet format whitespace <proj> --include <dir>/ --verify-no-changes; echo "exit=$?"
dotnet format style      <proj> --include <dir>/ --verify-no-changes; echo "exit=$?"
```

## 4. Amend or new commit

A PR exists, so the branch is almost certainly pushed. Confirm before touching history:

```bash
git rev-parse --abbrev-ref '@{u}'
git log --oneline '@{u}'..HEAD
```

If the branch tracks a remote and has nothing unpushed, add a **new** commit. Never amend or rebase what is already pushed - reviewers lose their place and the review threads detach. Check `git branch --show-current` right before committing, as always.

## 5. Reply in-thread

Use the replies endpoint so the answer threads under the finding instead of starting a loose conversation comment:

```bash
gh api repos/Devolutions/UniGetUI/pulls/<N>/comments/<comment_id>/replies --input reply.json --jq '.html_url'
```

Build `reply.json` with Python rather than inlining the body as a shell flag - review replies contain backticks, quotes, and code fences that shell quoting mangles:

```python
import json
body = "\n".join([
    "First line.",
    "",
    "Another paragraph.",
])
json.dump({"body": body}, open("reply.json", "w"))
```

Delete the file afterwards so it does not land in `git status`.

### What the reply should say

It posts under the user's own account, so write as the PR author: first person, plain, no bot-to-bot formality and no sign-off.

Lead with what the finding got right - it usually got something right, and starting there makes the correction that follows read as collaboration rather than defensiveness. Then correct any wrong mechanism with the specific evidence: name the guard, the ordering, the caller. Note if the behaviour predates the PR. Close with what actually changed, as a short code block if that is clearest, plus any extra benefit the fix brings.

Neither capitulate to a claim you have disproved nor argue with one you have confirmed. A reviewer reading the thread later should be able to tell exactly what was true.

## 6. Stop before pushing

Report the commit and that it is local. Flag the mismatch explicitly when it matters: if your reply describes a fix that is not pushed yet, the thread will not line up with the diff until it is, and the user should know that before a human reviewer looks. Then offer to push.
