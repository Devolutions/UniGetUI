# Contributing to UniGetUI

Thank you for helping improve UniGetUI. Please follow the [Code of Conduct](CODE_OF_CONDUCT.md) in all project spaces.

## Issues and feature requests

Use the GitHub issue forms and choose the template that best matches your request:

- **Bug or issue:** use the bug template, include the UniGetUI version, platform details, logs, and reproduction steps when available.
- **Hard crash:** use the hard-crash template.
- **New feature:** use the feature request template.
- **Improvement to an existing feature:** use the enhancement/improvement template.

Before opening an issue, search for duplicates and check whether the problem is specific to UniGetUI. If the same behavior can be reproduced directly with the underlying package manager or package, report it to that project first.

Do not include secrets, tokens, personal data, or private logs in public issues. Security vulnerabilities should be reported through the [Devolutions security page](https://devolutions.net/security/). Private non-security inquiries can be sent through the [Devolutions contact page](https://devolutions.net/contact/).

Feature requests and improvements are reviewed by the maintainers and prioritized based on scope, impact, maintainability, and the project roadmap.

## Pull requests

- Use the pull request template and describe the user-visible change.
- Keep pull requests focused on one feature, bug fix, or documentation update.
- Link related issues when applicable, but do not create placeholder issues just to justify a pull request.
- Mark unfinished work as a draft pull request.
- Make sure the project builds and the affected behavior is tested before requesting review.
- Spam, unrelated, non-building, or low-effort pull requests may be closed without review.

## Development setup and validation

Run the pre-commit hook setup once after cloning:

```powershell
pwsh ./scripts/install-git-hooks.ps1
```

The hook runs whitespace formatting on staged files under `src` when the `dotnet` CLI is available. If it rewrites files, review the changes and commit again.

Useful local validation commands from the repository root:

```powershell
dotnet format whitespace src --folder --verify-no-changes
dotnet restore src/UniGetUI.Windows.slnx
dotnet format style src/UniGetUI.Windows.slnx --no-restore --verify-no-changes
dotnet test src/UniGetUI.Windows.slnx --no-restore --verbosity q --nologo /p:Platform=x64
```

### Comparing build runners

The **Build runner benchmark** workflow (`.github/workflows/benchmark-runners.yml`)
compares GitHub-hosted Windows and Linux x64 runners with 4 vCPUs. It builds the
Release `win-x64` managed Avalonia app and its production project references, then
publishes that app with NativeAOT. Test projects, the separate elevated policy
helper, bundled CLI staging, installers and signing are excluded. The Windows target
framework is explicit on both hosts. The Windows-only C#/WinRT projection tool
runs once in a preparation job; both benchmark jobs compile those same generated
sources. Existing host-specific theme references and resource copying remain.

Managed restore, `dotnet build --no-restore`, NativeAOT restore and
`dotnet publish --no-restore` have separate timed steps. NativeAOT runs only after
managed compilation succeeds and reuses that build and package cache. Each runner uses
an isolated SDK installation and fresh NuGet package directory without cache
restore. SDK setup, projection preparation and artifact transfers are not timed.
The comparison appears in the workflow summary and the `benchmark-comparison`
artifact; per-phase JSON artifacts also record failures. Repeat runs to account
for hosted-runner and network variability.

NativeAOT imports the pinned
[mamoreau-devolutions/AotAnywhere](https://github.com/mamoreau-devolutions/AotAnywhere)
fork at `7661138aebfac938e4e6cde9d5e7fc6684c74690`, only in benchmark builds.
The preparation job builds its SDK package with MIT CRT stubs and symbol-only
Windows import libraries; it does not redistribute Microsoft SDK/CRT binaries.
Linux links with the fork's checksum-pinned LLVM 22.1.4; Windows uses MSVC.
Toolchain download, generation and packaging are excluded from the timings.
Both outputs must be native x64 PE images with no CLR header, and both undergo
a Windows CLI-startup smoke check. They are benchmark artifacts, not releases.
The different native linkers/CRT support and existing host-specific resources
mean this is a build-throughput comparison, not a byte-identical binary comparison.

Run it manually with `capture_binlog` enabled to upload four phase-specific
MSBuild binlogs per runner. Logs add measurement overhead and can contain source paths and
build properties; no repository secrets are passed to this workflow, and embedded
project imports are disabled. Compare runs with the same logging setting.

The path-filtered `push` trigger runs on any branch when the workflow,
benchmark scripts/props, SDK configuration or `src` changes, so it can be
tested before merging to `main`.
For a push-triggered run with binlogs, include `[benchmark-binlog]` in the head
commit message. After discovery, manual dispatch can select the benchmark branch.

## Coding guidelines

UniGetUI is primarily a C#/.NET Avalonia application. Follow the existing codebase style and patterns:

- Use PascalCase for types, methods, and properties.
- Follow existing private field conventions, including `_singleUnderscore` and `__doubleUnderscore` prefixes.
- Keep nullable reference types and type safety intact; avoid unnecessary casts and broad catch blocks.
- Use the `_UnSafe` suffix for internal unsafe package-manager methods when matching existing package-engine patterns.
- Localize user-facing strings with `CoreTools.Translate(...)`.
- Use the existing logging APIs from `UniGetUI.Core.Logging`.
- Keep changes focused and avoid unrelated refactors.

## Commits

- Keep each commit focused on a single logical change.
- Do not leave the project in a broken or non-buildable state between commits.
- Use clear commit messages and reference related issues when applicable.
