# Explicit shared-skill mirroring

Only `skeptical-reviewer` and `code-compressor` are approved sync-owned
directories. `.github/skills-sync.yml` is strict JSON (valid YAML), pinned to a
40-character commit from public `Devolutions/devolutions-gateway`. No patterns,
arbitrary destinations/repositories, or whole-tree replacement are supported.
Existing translation skills and all other repository content are not owned.

`sync.js` uses only Node built-ins. With Node 24 and a local upstream checkout:

```powershell
node .github\actions\sync-skills\sync.js UPSTREAM_CHECKOUT TARGET_CHECKOUT .github\skills-sync.yml preview
# Explicit local application, never pushes or opens a PR:
node .github\actions\sync-skills\sync.js UPSTREAM_CHECKOUT TARGET_CHECKOUT .github\skills-sync.yml apply
# Offline tests (from repository root):
Push-Location .github\actions\sync-skills
node --test test/*.test.js
Pop-Location
```

The CLI verifies the upstream checkout HEAD against the manifest before reading
skill contents. The exported `syncSkills({sourceDirectory, targetDirectory,
manifest, mode = "preview"})` supports offline fixtures; its caller is responsible
for verifying source revision. Both return/print a deterministic JSON change list
without printing skill content. The composite action has the same four inputs:
`source-directory`, `target-directory`, `manifest`, and `mode`; see `action.yml`.
It requires Node on PATH and git for revision verification.

Before any write, both owned trees are inspected. Traversal/unapproved manifest
paths, symlinks/junctions including ancestors, hard-linked/non-regular files,
unsafe portable filenames, and bounded-tree violations fail closed. Each tree
has a maximum of 256 entries, 2 MiB file content, and 16 nested directory levels.
Root license files are separately bounded to 2 MiB each. Upstream SKILL.md is
copied verbatim, root MIT/Apache licenses are retained, and UPSTREAM.md records
repository, directory and exact revision. A generated per-skill `.gitattributes`
disables text conversion to preserve upstream bytes on Windows checkouts too.

`apply` replaces only the explicitly named skill directories after preflight.
Use a dedicated, non-concurrently-mutated checkout: it is not a filesystem
transaction or a defense against another local process racing preflight/write.
I/O failure during application may leave an owned directory partially updated;
rerun from a clean, trusted checkout. Unowned content is never deleted.

The manual `Shared skills preview` workflow has read-only permissions, no
schedule, no provider credentials and no publication mode. It checks out the
manifest-pinned public upstream without persisted credentials, runs offline
tests and prints the preview. To update, review a new public upstream revision,
edit the manifest, then preview/apply locally and review the resulting diff.
No bot identity, environment or repository variables are required or configured.

## Requirement coverage

Validated on Windows with Node 24.22.0; Ubuntu execution is deferred to CI.
From `.github\actions\sync-skills`, `node --test test/*.test.js` passes all 18
tests, with zero skipped/failed. On the Node 22 host the exact successful command
was `npx --yes --package=node@24 --call "node --test test/*.test.js"`.
A preview against raw files downloaded from the pinned public revision returned
`changes: []`; the six existing translation directories have no Git diff.

| Requirement | Exact test evidence |
| --- | --- |
| No writes by default | `preview defaults to zero writes and reports exact owned changes` |
| Explicit ownership and six local translation skills preserved | `apply replaces only explicit skills and preserves all six translation trees`; `subset ownership never removes an omitted approved directory` |
| No broad ownership or traversal | `manifest rejects traversal absolute paths unapproved trees and broad ownership` |
| Source and destination symlinks/junctions | `rejects source symlinks and junctions without touching outside trees`; `rejects target symlinks and junctions without touching outside trees` |
| Ancestor/root links | `rejects ancestor symlinks and junctions without touching outside trees`; `rejects root symlinks and junctions without touching outside trees` |
| Preflight before mutation | `missing later source fails preflight before earlier skill replacement` |
| Revision pinning and CLI default | `CLI verifies the pinned commit and defaults to preview without writes` |
