---
name: translation-status
description: Analyzes checked-in UniGetUI language files to calculate translation percentages, surface untranslated coverage data, and generate localization status reports. Boundary-aware: completion is computed from the active English section above the legacy marker.
---

# translation status

Use this skill when the user asks for UniGetUI translation progress, localization or i18n coverage, untranslated-language reports, or a language support status summary.

## Prerequisites

- PowerShell 7 (`pwsh`).

## Scripts

- `scripts/get-translation-status.ps1`: Skill wrapper you should invoke from the skill.
- `../../../../scripts/translation/Get-TranslationStatus.ps1`: Canonical repository implementation delegated to by the wrapper.

## Usage

Show the default table summary:

```powershell
pwsh ./.agents/skills/translation-status/scripts/get-translation-status.ps1
```

Show only incomplete languages as markdown:

```powershell
pwsh ./.agents/skills/translation-status/scripts/get-translation-status.ps1 \
  -OutputFormat Markdown \
  -OnlyIncomplete
```

Write JSON output to a file:

```powershell
pwsh ./.agents/skills/translation-status/scripts/get-translation-status.ps1 \
  -OutputFormat Json \
  -OutputPath ./generated/translation-status.json
```

## Output Fields

- `Code`: language code
- `Language`: display name
- `Completion`: computed completion percentage using English active keys only
- `Translated`, `Missing`, `Empty`, `SourceEqual`, `Extra`: per-language entry counts for the active baseline
- `Legacy`: count of locale keys stored below the legacy boundary marker
- `Stored` and `Delta`: stored percentage metadata and difference from the computed result

## Notes

- The script treats source-equal values as untranslated for non-English languages.
- When `lang_en.json` contains the legacy boundary marker, completion excludes legacy compatibility keys below the marker.
- `Extra` excludes shared English legacy keys and only counts locale-only keys not present in English active or legacy sections.
- Use `-IncludeEnglish` if you want the `en` row included in the report.
- Use `-OnlyIncomplete` to focus on languages that still need work.
- For the full parameter surface, inspect `../../../../scripts/translation/Get-TranslationStatus.ps1`.