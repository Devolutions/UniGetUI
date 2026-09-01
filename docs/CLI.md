# UniGetUI command-line interface

This file documents the **public command-line surface** exposed by UniGetUI in the 2026 CLI redesign.

- For the background IPC API that powers these commands, see [IPC.md](IPC.md).
- For portable installations and where UniGetUI stores its data, see [PORTABLE.md](PORTABLE.md).
- For developer-only Avalonia diagnostics toggles, see the project source and build props; they are intentionally not documented here as public CLI arguments.

## Quick start

```powershell
unigetui status
unigetui app status
unigetui package search --manager dotnet-tool --query dotnetsay
unigetui package install --manager dotnet-tool --id dotnetsay --version 2.1.4 --scope Global
unigetui operation wait --id 123 --timeout 300
```

## Global transport options

These options select how the CLI connects to the local UniGetUI automation session.

| Option | Meaning |
| --- | --- |
| `--transport {named-pipe\|tcp}` | Client-side transport override. Default is `named-pipe`. |
| `--tcp-port <port>` | Client-side TCP port override. Used only with `tcp`. |
| `--pipe-name <name-or-path>` | Client-side named-pipe override. On Windows this is a pipe name. On non-Windows a relative name resolves under `/tmp`, while an absolute path uses that exact Unix socket path. |

Related environment variables:

| Variable | Meaning |
| --- | --- |
| `UNIGETUI_IPC_API_TRANSPORT` | Same as `--transport`. |
| `UNIGETUI_IPC_API_PORT` | Same as `--tcp-port`. |
| `UNIGETUI_IPC_API_PIPE_NAME` | Same as `--pipe-name`. |

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success |
| `1` | Command failed |
| `2` | Invalid parameter |
| `3` | IPC API unavailable |
| `4` | Unknown automation command |

## Command grammar notes

- Command nouns accept singular or plural forms: `operation`/`operations`, `package`/`packages`, `manager`/`managers`, `source`/`sources`, `shortcut`/`shortcuts`, `log`/`logs`, `backup`/`backups`, and `bundle`/`bundles`.
- `startmenu` is accepted as a spelling of `start-menu`, and `folders` as a spelling of `folder`.
- Compatibility aliases are accepted for some flags:
  - `--id` maps to `--package-id` or `--operation-id` where appropriate
  - `--source` maps to `--package-source`
  - `--source-name` and `--source-url` map to `--name` and `--url` on `source add` and `source remove`
  - `--name` maps to `--key` on `backup cloud download` and `backup cloud restore`
- Boolean options use explicit values such as `--enabled true` or `--wait false`.
- `--detach` is shorthand for asynchronous package operations (`--wait false`).
- `--manager` uses stable manager ids, not GUI labels. Current ids: `apt`, `bun`, `cargo`, `chocolatey`, `dnf`, `dotnet-tool`, `flatpak`, `homebrew`, `npm`, `pacman`, `pip`, `pwsh`, `scoop`, `snap`, `vcpkg`, `winget`, and `winps`.

## Command reference

### Core

| Command | Required options | Optional options | Notes |
| --- | --- | --- | --- |
| `status` | None | None | Returns transport, endpoint, and build information for the selected automation session. |
| `version` | None | None | Returns the UniGetUI build number through the IPC API. |

### App

| Command | Required options | Optional options | Notes |
| --- | --- | --- | --- |
| `app status` | None | None | Returns app/session state such as headless mode, page, and supported UI actions. |
| `app show` | None | None | Shows and focuses the window when a GUI session exists. |
| `app navigate` | `--page <page>` | `--manager <id>`, `--help-attachment <path>` | Valid pages include `discover`, `updates`, `installed`, `bundles`, `settings`, `managers`, `own-log`, `manager-log`, `operation-history`, `help`, `release-notes`, and `about`. |
| `app quit` | None | None | Gracefully shuts down the selected session, including headless daemons. |

### Operations

| Command | Required options | Optional options | Notes |
| --- | --- | --- | --- |
| `operation list` | None | None | Lists tracked live and completed operations. |
| `operation get` | `--id <operation-id>` | None | Returns the full tracked payload for one operation. |
| `operation output` | `--id <operation-id>` | `--tail <n>` | Reads captured output lines for one operation. |
| `operation wait` | `--id <operation-id>` | `--timeout <seconds>`, `--delay <seconds>` | Polls until the operation reaches a terminal state. |
| `operation cancel` | `--id <operation-id>` | None | Cancels a queued or running operation. |
| `operation retry` | `--id <operation-id>` | `--mode <mode>` | Retry modes are defined by the operation payload. |
| `operation reorder` | `--id <operation-id>`, `--action <run-now\|run-next\|run-last>` | None | Reorders a queued operation. |
| `operation forget` | `--id <operation-id>` | None | Removes a finished operation from the live tracked list. |

### Managers

| Command | Required options | Optional options | Notes |
| --- | --- | --- | --- |
| `manager list` | None | None | Lists managers and their automation-relevant capability flags. |
| `manager maintenance` | `--manager <id>` | None | Returns maintenance metadata for one manager. |
| `manager reload` | `--manager <id>` | None | Reloads one manager. |
| `manager set-executable` | `--manager <id>`, `--path <path>` | None | Sets a custom executable override, then reloads the manager. |
| `manager clear-executable` | `--manager <id>` | None | Clears the custom executable override, then reloads the manager. |
| `manager action` | `--manager <id>`, `--action <action>` | `--confirm` | Runs a manager-specific maintenance action. |
| `manager enable` | `--manager <id>` | None | Enables the manager. |
| `manager disable` | `--manager <id>` | None | Disables the manager. |
| `manager notifications enable` | `--manager <id>` | None | Enables update notifications for the manager. |
| `manager notifications disable` | `--manager <id>` | None | Disables update notifications for the manager. |

### Sources

| Command | Required options | Optional options | Notes |
| --- | --- | --- | --- |
| `source list` | None | `--manager <id>` | Lists sources, optionally filtered to one manager. |
| `source add` | `--manager <id>`, `--name <source-name>` | `--url <source-url>` | Adds a source. |
| `source remove` | `--manager <id>`, `--name <source-name>` | `--url <source-url>` | Removes a source. |

### Settings

| Command | Required options | Optional options | Notes |
| --- | --- | --- | --- |
| `settings list` | None | None | Lists non-secure settings. |
| `settings get` | `--key <key>` | None | Reads one non-secure setting. |
| `settings set` | `--key <key>` | `--enabled true\|false`, `--value <text>` | Sets either the boolean or string form of a setting. |
| `settings clear` | `--key <key>` | None | Clears a string-backed setting. |
| `settings reset` | None | None | Resets non-secure settings. |
| `settings secure list` | None | `--user <name>` | Lists secure settings for the current or specified user. |
| `settings secure get` | `--key <key>` | `--user <name>` | Reads one secure setting. |
| `settings secure set` | `--key <key>`, `--enabled true\|false` | `--user <name>` | Enables or disables one secure setting. |

Available keys live in:

- [`src/UniGetUI.Core.Settings/SettingsEngine_Names.cs`](src/UniGetUI.Core.Settings/SettingsEngine_Names.cs)
- [`src/UniGetUI.Core.SecureSettings/SecureSettings.cs`](src/UniGetUI.Core.SecureSettings/SecureSettings.cs)

### Shortcuts

| Command | Required options | Optional options | Notes |
| --- | --- | --- | --- |
| `shortcut list` | None | None | Lists tracked desktop shortcuts and stored keep/delete verdicts. |
| `shortcut set` | `--path <path>`, `--status <keep\|delete>` | None | Marks a shortcut to keep or delete. |
| `shortcut reset` | `--path <path>` | None | Clears the stored verdict for one shortcut. |
| `shortcut reset-all` | None | None | Clears all stored shortcut verdicts. |

### Start Menu shortcuts

Windows only. Deletion verdicts are keyed by shortcut path and are re-applied whenever an upgrade recreates the shortcut. Paths outside a Start Menu `Programs` directory, and paths that are not a `.lnk` or `.url` shortcut, are rejected.

| Command | Required options | Optional options | Notes |
| --- | --- | --- | --- |
| `start-menu shortcut list` | None | None | Lists tracked Start Menu shortcuts and stored keep/delete verdicts. |
| `start-menu shortcut set` | `--path <path>`, `--status <keep\|delete>` | None | Marks a Start Menu shortcut to keep or delete. |
| `start-menu shortcut reset` | `--path <path>` | None | Clears the stored verdict for one shortcut. |
| `start-menu shortcut reset-all` | None | None | Clears every stored verdict. Folder rules and the relocations they recorded are left alone. |

### Start Menu folders

Windows only. A folder rule names the subfolder of the current user's Start Menu `Programs` directory where a package should keep its shortcuts. UniGetUI re-applies it after every install and upgrade, and deletes the relocated shortcuts when the package is uninstalled.

`--package` is the rule key, in `manager\PackageId` form, for example `winget\Python.Python.3.13`; the manager segment is lower-cased. Note that the key carries no source, unlike package equivalence elsewhere in UniGetUI, so two packages sharing a manager and an id but coming from different sources share a single rule and cannot be placed in different folders. `--folder` must be a subfolder of the Start Menu `Programs` directory, so an absolute path, a `..` segment or the machine-wide directory is rejected.

| Command | Required options | Optional options | Notes |
| --- | --- | --- | --- |
| `start-menu folder list` | None | None | Lists the stored folder rules, plus any package that only has pending shortcuts. The latter are reported with an empty folder, so a script that wants actual rules has to skip them. |
| `start-menu folder set` | `--package <manager\id>`, `--folder <name>` | `--relocate-existing` | Stores a folder rule. `--relocate-existing` also moves the shortcuts that already match the package. |
| `start-menu folder remove` | `--package <manager\id>` | None | Removes the folder rule for one package. |

### Logs

| Command | Required options | Optional options | Notes |
| --- | --- | --- | --- |
| `log app` | None | `--level <n>` | Returns structured application log entries. |
| `log operations` | None | None | Returns persisted operation history. |
| `log manager` | None | `--manager <id>`, `--verbose` | Returns manager task logs. |

### Backups

| Command | Required options | Optional options | Notes |
| --- | --- | --- | --- |
| `backup status` | None | None | Returns backup settings and cloud-auth state. |
| `backup local create` | None | None | Creates a local backup bundle. |
| `backup github login start` | None | `--launch-browser` | Starts the GitHub device flow. |
| `backup github login complete` | None | None | Completes the pending device flow. |
| `backup github logout` | None | None | Clears the stored GitHub auth token. |
| `backup cloud list` | None | None | Lists cloud backups in the authenticated GitHub backup store. |
| `backup cloud create` | None | None | Uploads the current backup to cloud storage. |
| `backup cloud download` | `--key <name>` | None | Downloads one cloud backup as bundle content. |
| `backup cloud restore` | `--key <name>` | `--append` | Imports one cloud backup into the current in-memory bundle. |

### Bundles

| Command | Required options | Optional options | Notes |
| --- | --- | --- | --- |
| `bundle get` | None | None | Returns the current in-memory bundle. |
| `bundle reset` | None | None | Clears the current in-memory bundle. |
| `bundle import` | None | `--path <path>`, `--content <text>`, `--format <ubundle\|json\|yaml\|xml>`, `--append` | Imports bundle content from a file or raw content. |
| `bundle export` | None | `--path <path>` | Exports the current bundle, optionally to disk. |
| `bundle add` | `--id <package-id>` | `--manager <id>`, `--source <source>`, `--version <version>`, `--scope <scope>`, `--pre-release`, `--selection <search\|installed\|updates\|auto>` | Resolves a package and adds it to the bundle. |
| `bundle remove` | `--id <package-id>` | `--manager <id>`, `--source <source>`, `--version <version>`, `--scope <scope>`, `--pre-release`, `--selection <mode>` | Removes matching package entries from the bundle. |
| `bundle install` | None | `--include-installed true\|false`, `--elevated true\|false`, `--interactive true\|false`, `--skip-hash true\|false` | Installs the bundle through UniGetUI’s shared operation pipeline. |

### Packages

| Command | Required options | Optional options | Notes |
| --- | --- | --- | --- |
| `package search` | `--query <text>` | `--manager <id>`, `--max-results <n>` | Searches packages. |
| `package details` | `--id <package-id>` | `--manager <id>`, `--source <source>` | Returns the package details payload. |
| `package versions` | `--id <package-id>` | `--manager <id>`, `--source <source>` | Returns installable versions when supported by the manager. |
| `package installed` | None | `--manager <id>` | Lists installed packages. |
| `package updates` | None | `--manager <id>` | Lists available updates. |
| `package install` | `--id <package-id>` | `--manager <id>`, `--source <source>`, `--version <version>`, `--scope <scope>`, `--pre-release`, `--elevated true\|false`, `--interactive true\|false`, `--skip-hash true\|false`, `--architecture <value>`, `--location <path>`, `--wait true\|false`, `--detach` | Installs a package. Async mode returns an operation id immediately. |
| `package download` | `--id <package-id>` | `--manager <id>`, `--source <source>`, `--version <version>`, `--scope <scope>`, `--wait true\|false`, `--detach`, `--output <path>` | Downloads a package artifact. |
| `package reinstall` | `--id <package-id>` | Same options as `package install` | Re-runs installation for an installed package. |
| `package repair` | `--id <package-id>` | Same options as `package install`, plus `--remove-data true\|false` | Uninstalls then reinstalls the package. |
| `package update` | `--id <package-id>` | Same options as `package install` | Updates one package. |
| `package uninstall` | `--id <package-id>` | `--manager <id>`, `--source <source>`, `--scope <scope>`, `--remove-data true\|false`, `--elevated true\|false`, `--interactive true\|false`, `--wait true\|false`, `--detach` | Uninstalls a package. |
| `package show` | `--id <package-id>`, `--source <source>` | None | Opens the package details UI flow. |
| `package ignored list` | None | None | Lists ignored-update rules tracked by UniGetUI. |
| `package ignored add` | `--id <package-id>` | `--manager <id>`, `--version <version>`, `--source <source>` | Adds an ignored-update rule. |
| `package ignored remove` | `--id <package-id>` | `--manager <id>`, `--version <version>`, `--source <source>` | Removes an ignored-update rule. |
| `package update-all` | None | None | Queues updates for all currently upgradable packages. |
| `package update-manager` | `--manager <id>` | None | Queues updates for all upgradable packages handled by one manager. |

## Headless behavior

When UniGetUI is started with `--headless`, it exposes the same automation API without opening a window.

| Command | Headless behavior |
| --- | --- |
| `status`, `app status`, `app quit` | Fully supported. |
| `app show` | Fails with “the current UniGetUI session is running headless and has no window to show.” |
| `app navigate` | Fails with “the current UniGetUI session is running headless and cannot navigate UI pages.” |
| `package show` | UI-oriented; may fail or be meaningless in pure headless sessions. |
| `package update-all`, `package update-manager` | Require GUI-side upgrade handlers. Headless sessions may return “cannot update all packages” or “cannot update manager packages.” |

## Headless IPC options

When UniGetUI is started with `--headless`, these options control the IPC listener:

| Option | Meaning |
| --- | --- |
| `--ipc-api-transport {named-pipe\|tcp}` | Selects the server-side IPC transport. Default is `named-pipe`. |
| `--ipc-api-port <port>` | Overrides the TCP port when TCP transport is selected. |
| `--ipc-api-pipe-name <name-or-path>` | Overrides the server-side pipe name or Unix socket path. |

## Other application startup parameters

These parameters are accepted by the app executables in addition to the automation verb tree.

| Parameter | Meaning | Notes |
| --- | --- | --- |
| `--daemon` | Starts UniGetUI minimized to the notification area. | Requires the corresponding startup setting. |
| `--updateapps` | Forces automatic installation of available updates. | Historical compatibility flag. |
| `--uninstall-unigetui` | Unregisters UniGetUI from the notification panel and quits. | Historical; only valid for specific old versions. |
| `--uninstall-wingetui` | Unregisters the legacy WingetUI install from the notification panel and quits. | Historical; used by the WingetUI uninstaller. |
| `--migrate-wingetui-to-unigetui` | Migrates legacy WingetUI data and shortcuts, then quits. | Migration helper. |
| `--help` / `-h` | Prints CLI help. | For the direct verb-based CLI. |
| `--import-settings <file>` | Imports settings from a JSON file. | Existing settings are replaced. |
| `--export-settings <file>` | Exports settings to a JSON file. | Creates or overwrites the file. |
| `--enable-setting <key>` / `--disable-setting <key>` | Toggles one boolean setting. | Legacy setting flags. |
| `--set-setting-value <key> <value>` | Sets one string-backed setting. | Legacy setting flag. |
| `--no-corrupt-dialog` | Shows the verbose crash report instead of the simplified dialog. | Troubleshooting flag. |
| `--enable-secure-setting <key>` / `--disable-secure-setting <key>` | Toggles one secure setting for the current user. | May require elevation. |
| `--enable-secure-setting-for-user <user> <key>` / `--disable-secure-setting-for-user <user> <key>` | Toggles one secure setting for a specified user. | May require elevation. |
| `<bundle-file>` | Loads a valid bundle file into the Package Bundles page. | Supported extensions include `.ubundle`, `.json`, `.yaml`, and `.xml`. |

## Other environment variables

These are read by the application itself rather than by the CLI client.

| Variable | Values | Meaning |
| --- | --- | --- |
| `UNIGETUI_WINGET_CLI` | `default`, `winget`, `pinget` | Chooses which WinGet command-line tool the WinGet manager drives. Takes precedence over the `WinGetCliToolPreference` setting. |
| `UNIGETUI_WINGET_COM` | `default`, `enabled`/`enable`/`on`/`true`/`1`, `disabled`/`disable`/`off`/`false`/`0` | Forces the WinGet COM API on or off instead of letting UniGetUI decide. Takes precedence over the `WinGetComApiPolicy` setting. |
| `UNIGETUI_FONT_FAMILY` | A font family name | Windows only. Prepends a family to the UI font chain. Ignored when the "use the system UI font" setting is on, and an entry containing the Avalonia `$Default` family is discarded. |
| `UNIGETUI_FORCE_NATIVE_LINUX_DECORATIONS` | `1`/`true`/`on`/`yes`/`enabled`, `0`/`false`/`off`/`no`/`disabled` | Linux only. Forces the window manager's own title bar on or off instead of auto-detecting. An unrecognized value is ignored with a warning. |
| `UNIGETUI_GITHUB_TOKEN_NAMESPACE` | Any string | Suffixes the credential-store entry holding the GitHub backup token, so several UniGetUI instances on one machine can hold separate logins. |
| `WEBVIEW2_BROWSER_EXECUTABLE_FOLDER` | A directory path | Windows only. Points the embedded web view at a fixed-version WebView2 runtime instead of the installed evergreen one. |

## Deep links

The Windows installer registers a `unigetui://` protocol handler for regular installations; a portable install does not register it, see [PORTABLE.md](PORTABLE.md). It is used to route notification clicks back into a running instance, and accepts these actions:

| Deep link | Meaning |
| --- | --- |
| `unigetui://openUniGetUI` | Shows UniGetUI and brings the window to the front. |
| `unigetui://openUniGetUIOnUpdatesTab` | Shows UniGetUI on the Software Updates page. |
| `unigetui://updateAll` | Starts an update for every available package update. |
| `unigetui://releaseSelfUpdateLock` | Allows a pending UniGetUI self-update to proceed. |

Anything else after `unigetui://` is ignored. The action is only dispatched when UniGetUI is already running: a link that cold-starts the app launches it normally and the action is dropped. To drive UniGetUI programmatically, use the verb commands above or the [IPC API](IPC.md) rather than deep links.

## Installer parameters

The installer is Inno Setup based. It supports the standard [Inno Setup command-line parameters](https://jrsoftware.org/ishelp/index.php?topic=setupcmdline) plus these UniGetUI-specific switches:

| Parameter | Meaning |
| --- | --- |
| `/NoAutoStart` | Do not launch UniGetUI after installation. |
| `/NoRunOnStartup` | Do not register UniGetUI to start minimized at login. |
| `/NoVCRedist` | Skip installation of the MSVC x64 runtime. |
| `/NoEdgeWebView` | Skip installation of the Microsoft Edge WebView runtime. |
| `/NoChocolatey` | Deprecated no-op kept for compatibility. |
| `/EnableSystemChocolatey` | Deprecated no-op kept for compatibility. |
| `/NoWinGet` | Do not install WinGet and Microsoft.WinGet.Client if they are missing. |
| `/MSStore` | Microsoft Store install mode: skip the MSVC and WebView2 dependency installers, do not launch UniGetUI after installation, and disable startup at login. Use with `/CURRENTUSER` to select user-local scope. |

The installation type is an Inno Setup task rather than a switch. Pass `/TASKS="portableinstall"` for a portable installation; the default is `regularinstall`, which additionally accepts `regularinstall\startmenuicon` and `regularinstall\desktopicon`. A portable install keeps its settings beside the executable and registers no protocol handler, file association, shortcuts or startup entry, see [PORTABLE.md](PORTABLE.md).
