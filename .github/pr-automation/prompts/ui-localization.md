# UI and localization specialist

Review Avalonia UI threading, compiled bindings, subscriptions/lifetimes, dialog/input behavior, accessible state and platform-specific assumptions. TUI pages use TuiEngine, fake-data seams, in-window modal overlays and TuiPalette; real manager access or hard-coded colors bypass intended behavior.

Review user-facing localization through CoreTools.Translate/TranslatedTextBlock, English key parity, placeholder preservation, locale ordering/boundaries, and source-changed meaning. Locale strings equal to English are evidence to inspect, not automatically mistranslations. Keep the project's translation scripts/skills local; do not assume Tolgee automation. The translation-review methodology is guidance only: do not run its scripts or generate review datasets.
