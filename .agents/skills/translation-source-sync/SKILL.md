---
name: translation-source-sync
description: Synchronizes the UniGetUI English language file with source-code usage, identifies missing translation keys, removes unused entries, reports localization drift, and can reorder locale files to the English legacy-boundary layout. Use when the user asks to sync language files, missing translations, i18n drift, or align locale file ordering with English.
---

# translation source sync

Use this skill when UniGetUI source code changed and you need to keep [src/UniGetUI.Core.LanguageEngine/Assets/Languages/lang_en.json](src/UniGetUI.Core.LanguageEngine/Assets/Languages/lang_en.json) aligned with the strings actually used by the application, or when you need downstream locale files reordered to match the English active and legacy sections.

It scans supported C#, WinUI XAML, and Avalonia AXAML patterns, finds missing English keys, removes unused entries, reports translation-source warnings that still need manual cleanup, and can align locale file ordering to the English legacy-boundary layout.

## Scope

- Extract literal translation keys from supported UniGetUI source patterns.
- Add new English keys missing from `lang_en.json`.
- Remove English keys that are no longer used.
- Preserve legacy English keys below the reserved boundary marker when the boundary workflow is enabled.
- Reorder non-English locale files to match the English active and legacy key ordering.
- Warn about interpolated `CoreTools.Translate($"...")` calls that are not synchronized automatically.
- Leave downstream language propagation to the existing translation diff workflow.

## Supported extraction sources

- `CoreTools.Translate("...")` in C#.
- `CoreTools.AutoTranslated("...")` in C#.
- `widgets:TranslatedTextBlock Text="..."` in WinUI XAML.
- `settings:TranslatedTextBlock Text="..."` in Avalonia AXAML.
- `{t:Translate Some text}` and `{t:Translate Text='A, B, C'}` in Avalonia AXAML.

## Out of scope for v1

- Interpolated translation calls such as `CoreTools.Translate($"Running {value}")`.
- Non-literal translation inputs passed through variables.
- CI enforcement.

## Scripts

- `scripts/sync-translation-sources.ps1`: Skill wrapper around the repository sync script.
- `scripts/set-translation-boundary-order.ps1`: Skill wrapper around the repository locale reordering script.
- `scripts/test-translation-source-sync.ps1`: Skill wrapper around the repository smoke test.
- `../../../../scripts/translation/Sync-TranslationSources.ps1`: Canonical repository implementation used by the wrapper.
- `../../../../scripts/translation/Set-TranslationBoundaryOrder.ps1`: Canonical repository implementation used by the wrapper.
- `../../../../scripts/translation/Test-TranslationSourceSync.ps1`: Canonical smoke test used by the wrapper.

## Usage

Synchronize the checked-in English language file in place:

```powershell
pwsh ./.agents/skills/translation-source-sync/scripts/sync-translation-sources.ps1
```

Check whether the English file is out of sync without writing changes:

```powershell
pwsh ./.agents/skills/translation-source-sync/scripts/sync-translation-sources.ps1 -CheckOnly
```

Reorder locale files to match the English active section and legacy-boundary tail:

```powershell
pwsh ./.agents/skills/translation-source-sync/scripts/set-translation-boundary-order.ps1
```

Preview whether locale files need reordering without writing changes:

```powershell
pwsh ./.agents/skills/translation-source-sync/scripts/set-translation-boundary-order.ps1 -CheckOnly
```

Run the smoke test:

```powershell
pwsh ./.agents/skills/translation-source-sync/scripts/test-translation-source-sync.ps1
```

## Recommended workflow

1. Run translation source sync after changing any translatable source string.
2. If `-CheckOnly` reports drift, run the full sync to rewrite `lang_en.json`.
3. Review warnings for interpolated translation calls and convert them to stable literals or add the missing English keys manually when needed.
4. If you are using the legacy-boundary workflow, run `pwsh ./.agents/skills/translation-source-sync/scripts/set-translation-boundary-order.ps1` to align locale ordering with English.
5. Run `pwsh ./scripts/translation/Verify-Translations.ps1` to confirm the language files still validate cleanly.
6. Run `pwsh ./.agents/skills/translation-source-sync/scripts/test-translation-source-sync.ps1` if you changed the sync workflow itself.
7. Export changed work for translators with [translation-diff-export](../translation-diff-export/SKILL.md).

## Notes

- Existing English values are preserved for retained keys; newly added English entries default to `key == value`.
- The sync script preserves current key order for retained entries and appends newly discovered keys deterministically.
- The locale reorder script follows English as the canonical order and moves unmapped locale-only keys to the legacy section after the reserved boundary marker.
- The smoke test uses a temporary synthetic repo so it does not mutate the checked-in language files.