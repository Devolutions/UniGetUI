---
name: translation-diff-import
description: Merges translated key-value pairs from a UniGetUI JSON localization patch back into the full language file and validates the merged result. Use when the user asks to apply, merge, or import a translation patch.
---

# translation diff import

Use this skill after the export skill produced a `.source.json`, `.translated.json`, and `.reference.json` set and you want to merge the translated working copy back into the full target-language JSON file.

See [translation-diff-export](../translation-diff-export/SKILL.md) for the patch-generation step that produces the expected inputs.

## Prerequisites

- PowerShell 7 (`pwsh`).
- `cirup` available on `PATH`.

## Scripts

- `scripts/import-translation-diff.ps1`: Validates and merges a translated patch into the full language file.
- `scripts/validate-language-file.ps1`: Validates placeholder, token, HTML-fragment, and newline parity for either a full language file or just the active patch keys against `lang_en.json`.

## Usage

Import the translated French working copy from the export skill into the full French JSON file:

```powershell
pwsh ./.agents/skills/translation-diff-import/scripts/import-translation-diff.ps1 \
  -TranslatedPatch ./generated/translation-diff-export/lang.diff.fr.translated.json \
  -SourcePatch ./generated/translation-diff-export/lang.diff.fr.source.json \
  -TargetJson ./src/UniGetUI.Core.LanguageEngine/Assets/Languages/lang_fr.json \
  -NeutralJson ./src/UniGetUI.Core.LanguageEngine/Assets/Languages/lang_en.json \
  -OutputJson ./src/UniGetUI.Core.LanguageEngine/Assets/Languages/lang_fr.merged.json
```

Validate the merged output:

```powershell
pwsh ./.agents/skills/translation-diff-import/scripts/validate-language-file.ps1 \
  -NeutralJson ./src/UniGetUI.Core.LanguageEngine/Assets/Languages/lang_en.json \
  -TargetJson ./src/UniGetUI.Core.LanguageEngine/Assets/Languages/lang_fr.merged.json \
  -PatchJson ./generated/translation-diff-export/lang.diff.fr.source.json
```

Optional parameters:

- `-OutputDir` (default: `generated/translation-diff-import`)
- `-AllowUnchangedValues`
- `-KeepIntermediate`

Recommended input mapping from the export skill:

- `-TranslatedPatch`: `generated/translation-diff-export/lang.diff.fr.translated.json`
- `-SourcePatch`: `generated/translation-diff-export/lang.diff.fr.source.json`

## Output

- A merged `.json` file that contains both the previously translated entries and the imported patch values.
- If `-KeepIntermediate` is used, patch snapshots are preserved under `generated/translation-diff-import/tmp/`.

## Edge cases

- If the target JSON file does not exist yet, the script can create an output from the translated patch alone.
- When `-SourcePatch` is provided, the script validates translated keys, placeholder tokens, HTML-like fragments, newline counts, and likely untranslated values before delegating the merge to `cirup` while allowing missing keys for partial progress.
- The import skill expects `.translated.json` to remain sparse. Untranslated keys should be omitted instead of copied in English unless you intentionally bypass that check with `-AllowUnchangedValues`.
