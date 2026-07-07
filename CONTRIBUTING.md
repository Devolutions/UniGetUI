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
