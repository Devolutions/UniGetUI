---
name: translation-review
description: Reviews UniGetUI .json language files for localization quality, detects parity issues, English-equal entries, wrong-script content, and cross-language outliers, then generates a dataset for LLM-assisted linguistic review. Use when the user asks to review translations, audit localization, inspect i18n or l10n quality, or validate language files.
---

# translation review

Use this skill when you need a quality review of an existing UniGetUI translation, not just coverage, but correctness, language integrity, and consistency against related languages.

Typical triggers: review translations, check translation quality, mistranslations, wrong language in translation, untranslated strings, localization QA, translation audit, spot-check a language file.

## Scope

- Mechanically detect placeholder, HTML-fragment, and newline mismatches.
- Flag entries whose translation still equals the English source in a non-English language file.
- Detect wrong-script entries for non-Latin languages such as `ua`, `zh_CN`, `ar`, `ko`, `ja`, and `he`.
- Build a cross-language comparison table so the reviewing agent can identify outliers, suspicious wording, and wrong-language content.
- The agent performs the final linguistic review using the generated report.

## Prerequisites

- PowerShell 7 (`pwsh`).

## Scripts

- `scripts/review-translation-json.ps1`: Generate the review dataset for one UniGetUI language JSON file.

## Workflow

1. Generate the review dataset for the target language.
2. Confirm the output file exists and contains the expected flagged sections or JSON fields.
3. If the script fails or the output is empty, validate the target JSON with `pwsh ./scripts/translation/Verify-Translations.ps1` and rerun the review.
4. Read the flagged sections first: parity issues, English-equal entries, and wrong-script entries.
5. Spot-check the cross-language comparison table, focusing on labels, buttons, settings text, and error messages.
6. Report findings inline with the English source text, observed problem, and suggested correction.

## Usage

Generate a Markdown review report for French:

```powershell
pwsh ./.agents/skills/translation-review/scripts/review-translation-json.ps1 \
  -TargetJson ./src/UniGetUI.Core.LanguageEngine/Assets/Languages/lang_fr.json \
  -Language fr
```

Generate a report for Ukrainian and include script-detection checks:

```powershell
pwsh ./.agents/skills/translation-review/scripts/review-translation-json.ps1 \
  -TargetJson ./src/UniGetUI.Core.LanguageEngine/Assets/Languages/lang_ua.json \
  -Language ua
```

Generate a flagged-only JSON report with no cross-language table:

```powershell
pwsh ./.agents/skills/translation-review/scripts/review-translation-json.ps1 \
  -TargetJson ./src/UniGetUI.Core.LanguageEngine/Assets/Languages/lang_de.json \
  -Language de \
  -OutputFormat Json \
  -FlaggedOnly
```

Optional parameters:

- `-NeutralJson` — defaults to `src/UniGetUI.Core.LanguageEngine/Assets/Languages/lang_en.json`
- `-ComparisonLanguages` — languages to include in the comparison table; defaults to other checked-in `lang_*.json` files
- `-OutputPath` — defaults to `generated/translation-review/review.<lang>.md`
- `-OutputFormat` — `Markdown` (default) or `Json`
- `-FlaggedOnly` — skip the full cross-language table and emit only mechanically flagged entries

## Output

The Markdown report is structured as:

1. Summary
2. Parity Issues
3. English-Equal Entries
4. Wrong Script
5. Cross-Language Comparison

Default output path: `generated/translation-review/review.<lang>.md`

## Agent Review Guidelines

### Flagged sections

- Parity issues: confirm whether the placeholder, tag, or newline mismatch is a genuine error or an acceptable localized variant.
- English-equal entries: decide whether the English value is an acceptable technical or brand string, or whether it should be translated.
- Wrong script: almost always an error for non-Latin languages. Verify against comparison languages and flag for re-translation when confirmed.

### Cross-language comparison table

- Sample at least 20–30 rows and prefer entries that look like UI labels, buttons, dialogs, settings descriptions, or error messages.
- Look for wrong language, suspiciously different meaning, missing words, or copied English that should probably be translated.
- Ignore obvious brand names and technical identifiers.

### Output format for findings

Report each issue in this format:

```text
**Source**: `Some English source text`
**Problem**: [describe the issue]
**Current value**: [the problematic translation]
**Suggested correction**: [corrected value, if confident]
**Confidence**: High / Medium / Low
```

Group findings by type: parity, English-equal, wrong language, wrong script, or other.

## Integration with Other Skills

- Use [translation-status](../translation-status/SKILL.md) first if you need a coverage snapshot before reviewing quality.
- Use [translation-diff-export](../translation-diff-export/SKILL.md) to produce a sparse patch for entries that need re-translation after review.
- Use [translation-diff-import](../translation-diff-import/SKILL.md) to merge corrected entries back into the full language file.