---
name: translation-diff-export
description: Compares UniGetUI JSON locale files against English, identifies untranslated or source-changed keys, and generates patch, reference, and handoff files for a target language. Use when the user asks to export missing translations, untranslated strings, i18n diffs, or locale-file changes.
---

# translation diff export

Use this skill when you need a small UniGetUI translation patch instead of sending a full language JSON file for review or translation.

It exports only the active untranslated or source-changed strings, creates the translator handoff files, and prepares the patch set needed by `translation-diff-translate` and `translation-diff-import`.

## Prerequisites

- PowerShell 7 (`pwsh`).
- `git` available on `PATH`.
- `cirup` available on `PATH`.

When source strings changed, run `translation-source-sync` first so `lang_en.json` reflects current source usage before exporting downstream translation patches.

## Scripts

- `scripts/export-translation-diff.ps1`: Exports JSON patch artifacts and generates a translation handoff prompt.
- `scripts/test-translation-diff.ps1`: Runs a local end-to-end smoke test against the checked-in UniGetUI language files.

Use this skill's wrapper scripts first; the downstream translate and import steps are documented in [translation-diff-translate](../translation-diff-translate/SKILL.md) and [translation-diff-import](../translation-diff-import/SKILL.md).

## Usage

Export untranslated French strings only:

```powershell
pwsh ./.agents/skills/translation-diff-export/scripts/export-translation-diff.ps1 \
  -NeutralJson ./src/UniGetUI.Core.LanguageEngine/Assets/Languages/lang_en.json \
  -TargetJson ./src/UniGetUI.Core.LanguageEngine/Assets/Languages/lang_fr.json \
  -Language fr
```

Export untranslated French strings plus English source values changed since `origin/main`:

```powershell
pwsh ./.agents/skills/translation-diff-export/scripts/export-translation-diff.ps1 \
  -NeutralJson ./src/UniGetUI.Core.LanguageEngine/Assets/Languages/lang_en.json \
  -TargetJson ./src/UniGetUI.Core.LanguageEngine/Assets/Languages/lang_fr.json \
  -Language fr \
  -BaseRef origin/main
```

Optional parameters:

- `-OutputDir` (default: `generated/translation-diff-export`)
- `-KeepIntermediate`

Run the built-in smoke test:

```powershell
pwsh ./.agents/skills/translation-diff-export/scripts/test-translation-diff.ps1
```

## Output

For `lang_en.json` and language `fr`, the script generates:

- `generated/translation-diff-export/lang.diff.fr.source.json`
- `generated/translation-diff-export/lang.diff.fr.translated.json`
- `generated/translation-diff-export/lang.diff.fr.reference.json`
- `generated/translation-diff-export/lang.diff.fr.prompt.md`

If `-KeepIntermediate` is used, git-baseline snapshots are kept under `generated/translation-diff-export/tmp/`.

The smoke test writes its temporary artifacts under `generated/translation-diff-export-demo/`.

## Validate The Export

After export, confirm the patch is usable before handing it off:

1. Check that `.source.json` is not empty unless you expected no work for that language.
2. Confirm `.translated.json` is sparse and does not contain copied English placeholders for unfinished keys.
3. If the patch should include recent English changes, rerun with `-BaseRef` and compare the resulting `.source.json`.
4. Run the smoke test if you changed the export workflow itself.

## Hand Off To Translate

After exporting the patch, use the generated `.prompt.md` file with [translation-diff-translate](../translation-diff-translate/SKILL.md) to update the sparse translated working copy.

## Hand Off To Import

After translating the patch, merge it back into the full language file with [translation-diff-import](../translation-diff-import/SKILL.md):

```powershell
pwsh ./.agents/skills/translation-diff-import/scripts/import-translation-diff.ps1 \
  -TranslatedPatch ./generated/translation-diff-export/lang.diff.fr.translated.json \
  -SourcePatch ./generated/translation-diff-export/lang.diff.fr.source.json \
  -TargetJson ./src/UniGetUI.Core.LanguageEngine/Assets/Languages/lang_fr.json \
  -NeutralJson ./src/UniGetUI.Core.LanguageEngine/Assets/Languages/lang_en.json \
  -OutputJson ./src/UniGetUI.Core.LanguageEngine/Assets/Languages/lang_fr.merged.json
```

Keep the `.translated.json` file sparse. If a key is not translated yet, leave it out instead of copying the English source value into the working copy.