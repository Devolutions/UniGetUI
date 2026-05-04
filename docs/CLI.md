# UniGetUI command-line interface

This file documents the **public command-line surface** exposed by UniGetUI in the 2026 CLI redesign.

- For the background IPC API that powers these commands, see [IPC.md](IPC.md).
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

- Command nouns accept singular or plural forms: `operation`/`operations`, `package`/`packages`, `manager`/`managers`, and so on.
- Compatibility aliases are accepted for some flags:
  - `--id` maps to `--package-id` or `--operation-id` where appropriate
  - `--source` maps to `--package-source`
- Boolean options use explicit values such as `--enabled true` or `--wait false`.
- `--detach` is shorthand for asynchronous package operations (`--wait false`).
- `--manager` uses stable manager ids, not GUI labels. Current ids: `apt`, `cargo`, `chocolatey`, `dnf`, `dotnet-tool`, `flatpak`, `homebrew`, `npm`, `pacman`, `pip`, `pwsh`, `scoop`, `snap`, `vcpkg`, `winget`, and `winps`.

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
| `--welcome` | Opens the setup wizard. | Historical compatibility flag. |
| `--updateapps` | Forces automatic installation of available updates. | Historical compatibility flag. |
| `--report-all-errors` | Opens the error report page for any crash while loading. | Troubleshooting flag. |
| `--uninstall-unigetui` | Unregisters UniGetUI from the notification panel and quits. | Historical; only valid for specific old versions. |
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

## Deep links

UniGetUI also accepts the following `unigetui://` links:

| Deep link | Meaning |
| --- | --- |
| `unigetui://showPackage?id={id}&managerName={manager}&sourceName={source}` | Opens package details for the specified package. |
| `unigetui://showUniGetUI` | Shows UniGetUI and brings the window to the front. |
| `unigetui://showDiscoverPage` | Opens the Discover page. |
| `unigetui://showUpdatesPage` | Opens the Updates page. |
| `unigetui://showInstalledPage` | Opens the Installed page. |

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
