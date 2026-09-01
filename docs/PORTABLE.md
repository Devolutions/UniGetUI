# UniGetUI portable mode

This file documents **portable installations**, which keep UniGetUI's data beside the executable.

- For the public command-line interface, see [CLI.md](CLI.md).
- For the background IPC API, see [IPC.md](IPC.md).

By default UniGetUI keeps its configuration, caches and package metadata in a per-user directory
outside the installation folder. Portable mode moves all of that next to the executable, so the
whole application, settings included, can live on a removable drive or be copied between
machines. A few things deliberately stay outside that folder; see [What changes](#what-changes).

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

This is how you make the portable `.zip` and `.tar.gz` release archives actually portable.
They ship **without** the marker, so out of the box they still write to the per-user data
directory like a regular install.

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
| Default package-backup folder | `Documents\UniGetUI` | `Documents\UniGetUI` (**not** relocated) |

Package backups are one exception: their default location stays in the user's Documents
folder, and portable mode does not move it. Point it somewhere inside the portable folder from
the Backup settings page if you want backups to travel with the app.

The GitHub backup token is the other exception, and where it lives depends on the platform. On
macOS and Linux it is written to `SecureStorage` inside the data directory, so it travels with a
portable folder, as a plain file on disk. On Windows it is held in Credential Manager instead,
so a portable copy does not carry the login, and every portable copy on one machine shares
the same stored token unless `UNIGETUI_GITHUB_TOKEN_NAMESPACE` is set to separate them.

Portable mode also does not relocate anything owned by the package managers themselves. WinGet,
Scoop, Chocolatey, npm and the rest keep their own state in their usual per-user or system
locations, and the packages they install are installed normally.

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
