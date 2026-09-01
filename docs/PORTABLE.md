# UniGetUI portable mode

This file documents **portable installations**, which keep UniGetUI's data beside the executable.

- For the public command-line interface, see [CLI.md](CLI.md).
- For the background IPC API, see [IPC.md](IPC.md).

By default UniGetUI keeps its configuration, caches and package metadata in a per-user directory
outside the installation folder. Portable mode moves all of that next to the executable, so the
whole application, settings included, can live on a removable drive or be copied between
machines. A few things deliberately stay outside that folder; see [What changes](#what-changes).

The Windows `.zip` release ships in portable mode. The installer, and the macOS and Linux
archives, do not.

## Enabling portable mode

Portable mode is controlled by a single marker file named `ForceUniGetUIPortable`, placed in
the installation root next to the UniGetUI executable. The file's contents are ignored (the
one shipped by the installer is empty); only its presence matters.

### With the Windows installer

The installer offers “Perform a portable installation” as an installation type. Selecting
it copies the marker into the install directory. To choose it from a silent install, use Inno
Setup's standard `/TASKS` switch, and point `/DIR` at the location the portable copy should
live in:

```powershell
UniGetUI.Installer.exe /VERYSILENT /TASKS="portableinstall" /DIR="E:\UniGetUI"
```

`portableinstall` and `regularinstall` are mutually exclusive; `regularinstall` is the default.

The install directory has to be writable by the account that runs UniGetUI, or portable mode
silently falls back, as [described below](#fallback-when-the-folder-is-not-writable). The
installer defaults to per-user mode (`PrivilegesRequired=lowest`), so without `/DIR` it lands
in `%LOCALAPPDATA%\Programs\UniGetUI`, which is writable and works. An all-users install, chosen
in the dialog or with `/ALLUSERS`, lands in `C:\Program Files\UniGetUI` instead, where a
normally launched UniGetUI cannot create `Settings`.

### By hand

Create an empty file called `ForceUniGetUIPortable` (no extension) beside the executable:

```powershell
# Windows
New-Item -ItemType File -Path "C:\Path\To\UniGetUI\ForceUniGetUIPortable"
```

```bash
# macOS / Linux
touch /path/to/unigetui/ForceUniGetUIPortable
```

The Windows `.zip` already ships with the marker, so it is portable out of the box; creating
the file by hand is only needed for the macOS and Linux `.tar.gz` archives, which do not carry
it. Deleting the marker is the supported way to turn portable mode back off in the `.zip`, and
it is deliberately excluded from `IntegrityTree.json` so removing it cannot fail the integrity
check.

### Where the marker goes

The marker is looked up in the installation root, which is normally the directory holding
the executable. When the executable sits in an `Avalonia` subdirectory of a recognizable
install root, the parent directory is used instead, so the marker belongs one level up
alongside `UniGetUI.exe` and `IntegrityTree.json`.

On macOS the executable lives inside the `.app` bundle, so a marker placed there is discarded
whenever the bundle is replaced by an update. Re-create it after upgrading.

## What changes

| Data | Regular install | Portable install |
| --- | --- | --- |
| Root data directory | `%LOCALAPPDATA%\UniGetUI` on Windows; `~/Library/Application Support/UniGetUI` on macOS; `$XDG_DATA_HOME/UniGetUI`, else `~/.local/share/UniGetUI`, on Linux | `<install dir>\Settings` |
| Configuration | `<data dir>\Configuration` | `<install dir>\Settings\Configuration` |
| Per-package install options | `<data dir>\InstallationOptions` | `<install dir>\Settings\InstallationOptions` |
| Cached package metadata | `<data dir>\CachedMetadata` | `<install dir>\Settings\CachedMetadata` |
| Cached icons and screenshots | `<data dir>\CachedMedia` | `<install dir>\Settings\CachedMedia` |
| Cached language files | `<data dir>\CachedLanguageFiles` | `<install dir>\Settings\CachedLanguageFiles` |
| Stored secrets, macOS and Linux | `<data dir>/SecureStorage` | `<install dir>/Settings/SecureStorage` |
| Stored secrets, Windows | Credential Manager | Credential Manager (**not** relocated) |
| Session log, WebView2 profile, update logs | `%TEMP%\UniGetUI` on Windows; `$TMPDIR/UniGetUI` elsewhere | `<install dir>\Settings\Temp` |
| Default package-backup folder | `Documents\UniGetUI` | `<install dir>\Settings\Backups` |
| Elevated secure settings, Windows | `%ProgramFiles%\UniGetUI\SecureSettings` | `%ProgramFiles%\UniGetUI\SecureSettings` (**not** relocated) |

Package backups follow the portable folder, so they travel with the app. A path chosen on the
Backup settings page always wins over that default.

Two things deliberately stay put. Elevated secure settings — the toggles that permit CLI
arguments, custom manager paths and pre/post-operation commands — live under `%ProgramFiles%`
precisely because writing there needs administrator rights. Moving them into a user-writable
portable folder would let any process running as the user grant UniGetUI the right to execute
arbitrary commands, so they stay where they are.

The scratch directory holds files that are rebuilt on demand: the session log, the crash report
left behind for the next launch, the per-attempt auto-updater log, the WebView2 profile, and the
`%TEMP%` handed to package-manager subprocesses when UniGetUI runs elevated. Portable mode moves
it inside the portable folder, so a portable copy leaves nothing behind in the system temporary
directory. It is safe to delete while UniGetUI is not running.

The GitHub backup token is the second, and where it lives depends on the platform. On Windows it
is held in Credential Manager, which encrypts it per user and does not travel with the folder, so
a portable copy asks you to sign in on each machine. On macOS and Linux it is written to
`SecureStorage` inside the data directory **as a plain file**, so it does travel — treat a
portable folder carrying one as you would the token itself. Relocating the Windows token into the
portable folder would mean that same plaintext trade-off, on removable media, so it stays in
Credential Manager. Every portable copy on one machine shares the same stored token unless
`UNIGETUI_GITHUB_TOKEN_NAMESPACE` is set to separate them.

Portable mode also does not relocate anything owned by the package managers themselves. WinGet,
Scoop, Chocolatey, npm and the rest keep their own state in their usual per-user or system
locations, and the packages they install are installed normally.

## Importing settings from a per-user installation

A portable folder starts empty, so an existing installation's settings are not picked up
automatically — they stay in the per-user data directory, untouched.

The first time UniGetUI runs portable and finds settings there, it offers a one-time
**Import** action in a notification. Accepting copies `Configuration` and `InstallationOptions`
into the portable folder; caches are skipped because they are rebuilt on demand and are far
larger than the settings themselves. Nothing is overwritten and nothing is removed from the
source, so a per-user installation on the same machine keeps working. Restart UniGetUI
afterwards for the imported settings to take effect.

Dismissing the notification, or importing once, stops it from appearing again. This matters for
a portable copy carried between machines: it will never silently absorb the settings of a
machine it happens to be plugged into.

## What a portable install does not register

The Windows installer registers these only for a regular installation, so a portable install
gets none of them:

| Feature | Consequence when portable |
| --- | --- |
| `unigetui://` protocol handler | Deep links and notification-click actions are not routed by the shell. |
| `.ubundle` file association | Bundle files do not open in UniGetUI on double-click. Pass the path on the command line instead. |
| Start-at-login entry | UniGetUI does not start with Windows, and `--daemon` is not registered. |
| Start menu and desktop shortcuts | Not created. |

## Fallback when the folder is not writable

On first use of the data directory, UniGetUI verifies it can create and write inside
`<install dir>\Settings`. If that fails, for instance on an install under `Program Files`, a
read-only volume, or a locked-down drive, portable mode is **silently abandoned for that
session** and the normal per-user directory is used instead. The reason is recorded in the
**UniGetUI Log** (sidebar menu) as “Could not acces/write path”, spelled with one “s”
in the message itself.

Install to a location the running user can write, such as a removable drive or a folder under
the user profile, if you rely on portable mode.

## Turning portable mode off

Delete the `ForceUniGetUIPortable` file and restart UniGetUI. The app reverts to the per-user
data directory; the `Settings` folder is left on disk untouched, so copy anything you want to
keep out of it first. The check runs once per session, so a restart is required either way.
