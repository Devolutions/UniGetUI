# UniGetUI Terminal UI

`UniGetUI.Tui` is a terminal front-end for the UniGetUI package engine, built with
[Consolonia](https://github.com/Consolonia/Consolonia) (Avalonia 12 rendered to a console). It uses the
same engine, settings, secure settings, install options, ignored-updates database, bundles and
operation history as the desktop app, so a change made in one is seen by the other.

## Running

```shell
dotnet run --project src/UniGetUI.Tui/UniGetUI.Tui.csproj
dotnet run --project src/UniGetUI.Tui/UniGetUI.Tui.csproj -- --fake-data
```

| Option | Meaning |
| --- | --- |
| `--fake-data` | Run against the built-in fake data set (see below). Nothing real is installed. |
| `--fake-data-dir <dir>` | Same, but keep the sandbox in `<dir>` so fake state survives restarts. |
| `--page <id>` | Open on `discover`, `updates`, `installed`, `bundles`, `operations`, `managers`, `settings`, `logs`, `history`, `help` or `about`. |
| `--updateapps` | Update every upgradable package once the update check finishes. |
| `--theme <id>` | Use a colour theme for this session without saving it. `--help` lists the theme ids. |
| `<file.ubundle\|.json\|.yaml\|.xml>` | Open a package bundle on start. |
| `--import-settings`, `--export-settings`, `--enable-setting`, `--disable-setting`, `--set-setting-value`, `--enable-secure-setting`, `--disable-secure-setting` | The desktop settings commands. They run without opening the UI, then exit. |
| `--help` | Print the usage and exit. |

## Publishing (NativeAOT)

Release publishes are NativeAOT (trimmed, native executable), like the desktop app:

```
dotnet publish src/UniGetUI.Tui/UniGetUI.Tui.csproj -c Release -r win-x64 -p:Platform=x64 -m:1
```

The output is `src/UniGetUI.Tui/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/publish/UniGetUI.Tui.exe`.
`-m:1` avoids a parallel-build file lock on the WinGet project, which is referenced two different ways.

- Prerequisite on Windows: the Visual Studio "MSVC x64/x86 build tools" component (or the "Desktop development
  with C++" workload), which provides the native linker. Without it the publish fails with "Platform linker not found".
- Consolonia is not trim-safe (it loads XAML by `avares://` URI and creates internal Avalonia types by name), so
  trimmed builds keep its assemblies whole (`TrimmerRootAssembly` in `UniGetUI.Tui.csproj`) and `Program` keeps
  the two Avalonia types it creates by reflection. Grid columns use compiled (lambda) bindings for the same reason.
- To publish the JIT (ReadyToRun) build instead: add `-p:PublishAot=false -p:PublishTrimmed=false -p:PublishReadyToRun=true`.

Measured on the same commit, fake data, in a real terminal (median of 9–10 warm runs):

| Metric | ReadyToRun (JIT) | NativeAOT |
| --- | --- | --- |
| Launch to first screen | 725 ms | 252 ms |
| Launch to packages loaded | 727 ms | 421 ms |
| `--help` (start to exit) | 105 ms | 46 ms |
| Working set once loaded | 91 MB | 67 MB |
| Published size (without symbols) | 190 MB, 340 files | 60 MB, 69 files |

## Fake data mode

`--fake-data` (or `UNIGETUI_TUI_FAKE_DATA=1`) replaces every package manager with fake ones and
redirects all UniGetUI state into a sandbox under `%TEMP%\UniGetUI-TUI-FakeData\` (or
`--fake-data-dir`):

- **Managers.** WinGet, Scoop, Chocolatey, Pip and Npm are fake and "ready". Cargo is reported as not
  installed. Package names, publishers and URLs are fictitious (Contoso, Fabrikam, Northwind…, all on the
  reserved `.invalid` domain).
- **Operations** run through the real operation engine: options, the command line, process spawn, output
  streaming, verdicts, history, and the loader updates. The "package manager" they spawn is the TUI binary
  itself (`--fake-pm …`). It only edits the sandbox's `fake-system-state.json`.
- **Special packages.** Ids containing `Failing` always fail. Ids containing `Slow` take long enough to
  cancel. Elevation goes to a fake elevator (`--fake-elevate`).
- **Other paths.** Downloads are served from memory, so no network is used. Cloud backup uses a
  sandbox folder instead of GitHub.
- **Real-machine actions are refused.** The Devolutions Agent broker, desktop-shortcut management, the
  WinGet repair and the Scoop scripts are disabled, and "open" actions print their target instead of
  launching it.

## Keyboard

| Keys | Action |
| --- | --- |
| Alt+1 … Alt+9 (or Ctrl+1 … Ctrl+9) | Go to the numbered page tab: Discover, Updates, Installed, Bundles, Operations, Managers, Settings, Logs, History. Help (F1) and About are dialogs, opened from the Help menu. Clicking a tab works too. |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / previous page |
| Tab / Shift+Tab | Move between the controls of the page (focus always stays in the page) |
| Esc | Close a dialog or menu, otherwise return to the page's main control (e.g. from the filter to the list) |
| F10 or Alt+letter | Menu bar: Alt+F File, Alt+P Page, Alt+V View, Alt+O Operations, Alt+H Help. Inside an open menu, ↑ ↓ and Enter pick an item (items have no access letters; their global shortcut is shown on the right), ← → or Alt+letter switch menus. The menu's access letter is underlined and highlighted; letters are assigned at runtime from the translated labels, so every language gets unique keys. |
| F1, F5 or Ctrl+R, Ctrl+F or `/`, Ctrl+A, Ctrl+Q | Help, reload, search, select all, quit |
| Space, Enter, Ctrl+Enter, `o` or Alt+Enter, `m` | Package lists: select, details, main action, options, all actions |
| `i` `u` `x` `b` `g` `f` `s` | Install, update, uninstall, add to bundle, ignore updates, filter by source, sort |
| F3 | Package lists: search mode and options (the header's Mode chip). Clicking the Mode, Sources or Sort chip opens the same picker as F3, `f` or `s`. |

## Themes

Pick a theme in **Settings › User interface preferences › Theme** (Left/Right previews each one live) or
**View › Theme…**. The choice is saved (`TuiTheme` setting) and applied immediately, without a restart.

| Theme | Notes |
| --- | --- |
| Devolutions - Graphite | The original TUI look and the default: graphite grey with Devolutions blue chrome. |
| Devolutions - Black | True black and fully neutral: a pure black `#000000` page, `#0D0D0D` bars, grey panels, borders, focus and selection, white headings and access letters, no blue or yellow. Softer than High contrast. |
| Devolutions - Light | RDM Light: white surfaces, brand blue `#0068C3` title bar, light-blue focus with dark text. |
| Devolutions - Dark Blue | RDM's default dark ramp (`DarkBlue`): navy fills and navy chrome. |
| Devolutions - Blue | The same navy fills with Devolutions brand-blue title and menu bars. |
| Devolutions - Gray | RDM `DarkGray`. |
| Devolutions - High contrast | RDM `DarkHighContrast`: black, white text, light borders, yellow access keys. |
| Dracula, Monokai, Nord, Gruvbox Dark, Solarized Dark, Solarized Light, One Dark, Tokyo Night, Catppuccin Mocha | The projects' published palettes, with a few colours darkened or lightened where the original fails the contrast checks below. |

The Devolutions colours come from Remote Desktop Manager's colour tokens (`fill-*`, `border-*`, `text-*`
in its Light, DarkBlue, DarkGray and DarkHighContrast ramps).

For contributors:
- A theme is a set of semantic roles (`Theme/TuiTheme.cs`): text, muted and dim text, brand, chrome, panel frame,
  menu, focus, selection, edit surface, status colours, access key, and so on. `Theme/TuiPalette.cs`
  exposes them as shared brushes and overrides Consolonia's `Theme*Brush` resources.
- Never hard-code a colour in a control. Use a `TuiPalette` brush, so every theme (and a live switch)
  reaches it.
- `TuiThemeTests` checks WCAG contrast for every text/background pairing the UI draws, in every theme:
  body text at 7:1, other text at 4.5:1, and status colours, placeholders and access keys at 3:1.
- In the 16-colour legacy console (no `WT_SESSION`) Consolonia maps theme colours to the nearest
  console colour, so themes are approximate there.

## Feature parity with the desktop app

| Desktop feature | TUI |
| --- | --- |
| Discover / Updates / Installed / Bundles pages | ✓ Same loaders, main actions and per-page action lists (toolbar + context menu → `m` menu and F10 › Page) |
| Search modes (both, name, id, exact, similar), case, special characters, instant search | ✓ Search options dialog |
| Source filter tree | ✓ Filter-by-source dialog (per manager / source, including "Local") |
| Sorting (name, id, version, new version, source) persisted per page | ✓ `s` menu and column headers |
| Multi-select, select all, updates checked by default | ✓ Space, Ctrl+A, `DisableSelectingUpdatesByDefault` |
| As administrator / interactive / skip hash / remove data variants | ✓ |
| Reinstall, uninstall-then-reinstall, uninstall-then-update, update to version X | ✓ |
| Ignore updates, skip version, pause updates (1 day … 12 months), manage ignored updates | ✓ |
| Package details (all fields, dependencies, main action and variants) | ✓ |
| Installation options (profiles, follow defaults, version/arch/scope/location, CLI args, close apps, pre/post commands, live command preview, auto-update, ignore future updates) | ✓ One scrolling dialog instead of tabs |
| Manual install/update/uninstall command | ✓ Shown in a dialog to copy (no pre-filled terminal window) |
| Download installer(s) | ✓ |
| Open install location | ✓ Shows the path; Open launches it |
| Export to CSV, add to bundle, copy package info | ✓ |
| Installer host / download size columns | ✓ |
| Bundles: new, open (ubundle/json/yaml/xml), save, security report, create .ps1, remove, unsaved-changes guard, install already-installed setting | ✓ |
| Operations: live list and output, cancel, close, retry and retry variants, run now / next / last, bulk retry / clear / cancel, auto-remove succeeded, parallel limit | ✓ |
| Operation history: filters, revert, run again, retry variants, full log, copy, remove, clear | ✓ |
| Notifications (progress, success, error, updates available, batch summary) | ✓ In-app notification line, honouring the same settings |
| Settings: General, Interface, Notifications, Updates, Operations, Scheduled maintenance, Backup, Administrator (incl. secure settings), Internet (proxy and credentials), Experimental; import / export / reset | ✓ Every key; desktop-only ones are labelled "(desktop app)" |
| Package managers page: enable/disable, status, executable selection, default install options, notifications, minimum update age, logs, sources (add known/custom, remove), WinGet / Scoop / Bun / vcpkg extras | ✓ |
| Scheduled maintenance (check / install updates, local and cloud backups, run now, manage automatic updates) | ✓ |
| Local backup and cloud backup (GitHub gist) | ✓ GitHub sign-in uses the device flow (code + URL) |
| Logs: UniGetUI log with 5 levels, manager logs (+ verbose), copy, export | ✓ |
| Help, release notes, about (version, contributors, translators, licenses) | ✓ Links open in the browser |
| Command-line flags (`--updateapps`, bundle file, settings commands) | ✓ |
| Keyboard shortcuts | ✓ See above |
| Administrator warning, WinGet malfunction warning | ✓ As notifications |
| Manage desktop shortcuts | ✓ Windows, real data only |
| Theme, fonts, nav menu mode, tray icon, icons and illustrations, GPU rendering | Desktop-only visuals; the settings stay editable |
| Tray icon and OS toast notifications | Not applicable to a terminal |
| Self-update of UniGetUI | Desktop-only; Help links to the release page |
| Devolutions Agent policy inspector / editor | Desktop-only (the broker toggle itself is available) |
| Start Menu shortcut rules, integrity-violation and crash-report dialogs | Desktop-only |

## Tests

- `src/UniGetUI.Tui.Tests` runs the real TUI in Consolonia's in-memory test console, in fake data mode
  (`E2E/FakeDataSetUp.cs`), and drives it with keystrokes only. The tests read the rendered screen and, for
  effects that don't appear on screen, the fake system state file. They cover every page and dialog.
  Unit tests cover the command line, the fake package manager process and the fake catalog.
- Real-terminal checks: run `UniGetUI.Tui.exe --fake-data` under a ConPTY driver (for example node-pty
  with a headless xterm). This catches console-driver input problems that the in-memory console can't see.

## Terminal input notes

- On Windows the TUI unregisters Avalonia's clipboard service at startup. Consolonia's Win32 console
  otherwise routes every keystroke through a clipboard-paste detector that drops typed characters under
  ConPTY. Copy commands write to the Windows clipboard directly.
- AltGr characters (`\ @ { } [ ] | €` on many layouts) arrive as Ctrl+Alt chords. Consolonia never turns
  those into text, so the main window delivers the key's symbol as text itself.
- Alt+letter needs the terminal to send Alt as Meta (Esc prefix). That is the default in Windows Terminal,
  GNOME Terminal, iTerm2 and most others. macOS Terminal.app needs *Settings › Profiles › Keyboard ›
  Use Option as Meta key*. F10 always works. Windows Terminal binds Alt+Enter to full screen by default, so
  use `o` for installation options there.
- The underlined access letter needs ANSI output. On Windows, Consolonia uses it inside Windows Terminal
  (`WT_SESSION`) and falls back to the 16-colour legacy console (no underline, dimmer letter) elsewhere.
- `UNIGETUI_TUI_KEYLOG=<file>` (developer only) logs every key and text event as the terminal delivered it.
