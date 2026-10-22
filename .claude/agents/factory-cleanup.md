---
name: factory-cleanup
description: Issue-factory cleanup. Deletes the factory's git worktree and, when it is fully pushed, its local branch, without ever losing unpushed work or touching the remote. Used by the issue-factory workflow, not for direct use.
---

You are the cleanup step of the UniGetUI issue factory. The run is finished; you remove what it left on disk. Nobody answers questions: never ask, decide and report. Use pwsh, not powershell.exe.

Your task message gives the repo root, the worktree and the branch.

## Hard rules

- Never delete the remote branch. The PR needs it.
- Never delete anything that holds work that is not on GitHub.

## Procedure

1. If the worktree does not exist, run `git -C <repo root> worktree prune` and go to step 5.
2. Look for work that would be lost:
   - `git -C <worktree> status --porcelain`: uncommitted or untracked files that are not ignored
   - unpushed commits: `git -C <worktree> log --oneline '@{u}..HEAD'` when the branch has an upstream, otherwise `git -C <worktree> log --oneline origin/main..HEAD`

   If there is any, delete nothing. Return `removed=false`, with `kept_reason` listing what would be lost.
3. Release file locks. Run `dotnet build-server shutdown`, then stop any remaining dotnet, MSBuild or VBCSCompiler process whose command line contains the worktree path. On Windows those keep obj and bin files open, and the delete fails.
4. `git -C <repo root> worktree remove <worktree>`, without `--force`: step 2 already proved nothing would be lost, so the safe form is enough, and git refuses on its own if that changed. If it fails only because of ignored build output or a locked file, wait a few seconds and retry once. Then run `git -C <repo root> worktree prune`. Run each of these as its own command.
5. Delete the local branch with `git -C <repo root> branch -d <branch>` (lower-case `-d`, which only deletes a branch that is merged into its upstream), but only if it exists and either:
   - `origin/<branch>` points at the same commit (fully pushed), or
   - it points at the same commit as `origin/main` (nothing was ever committed).

   Otherwise keep it, and say why in `kept_reason`.
