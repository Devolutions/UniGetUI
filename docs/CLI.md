# UniGetUI command-line interface

This file documents the **public command-line surface** exposed by UniGetUI in the 2026 CLI redesign.

- For the background IPC API that powers these commands, see [IPC.md](IPC.md).
- For portable installations and where UniGetUI stores its data, see [PORTABLE.md](PORTABLE.md).
- For developer-only Avalonia diagnostics toggles, see the project source and build props; they are intentionally not documented here as public CLI arguments.

## Quick start

The `uniget` console launcher is included beside the GUI executable on Windows, Linux, and
macOS. It forwards arguments and terminal input/output, waits for each command to finish, and
returns its exit code. Keep it with the rest of the installation; copying only the launcher
elsewhere will not work.

- **Windows:** Keep **Add to PATH** selected during a regular installation to run `uniget` from
  any directory. This option is on by default (upgrades keep your previous choice) and uses the
  user or system `PATH` according to the install mode. Open a new terminal after installation. Without this option, run
  `.\uniget.exe` from the installation directory.
- **Linux:** The `.deb` and `.rpm` packages expose `uniget` on `PATH`; from an extracted tarball,
  run `./uniget` in the directory containing `UniGetUI`.
- **macOS:** The launcher is at `/Applications/UniGetUI.app/Contents/MacOS/uniget` after moving
  the app to `/Applications`; run it by its full path.

The commands below assume PowerShell is in the installation directory on Windows:

```powershell
.\uniget.exe status
.\uniget.exe tui
.\uniget.exe app status
.\uniget.exe package search --manager dotnet-tool --query dotnetsay
.\uniget.exe package install --manager dotnet-tool --id dotnetsay --version 2.1.4 --scope Global
.\uniget.exe operation wait --id 123 --timeout 300
.\uniget.exe skills --help
```

For a silent Windows installation, include `ADDTOPATH=0` to opt out, or `ADDTOPATH=1` to
explicitly select **Add to PATH** (for example, over a previously saved opt-out):

```powershell
.\UniGetUI.Installer.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART ADDTOPATH=0
```

The property name is case-insensitive and accepts only `0` or `1`. It overrides the saved
selection and `/TASKS` or `/MERGETASKS` for this checkbox only; other tasks are unchanged.
Without it, normal task-selection behavior applies. The existing
`/MERGETASKS="regularinstall\addtopath"` syntax remains supported.
Portable installations never change `PATH`, even with `ADDTOPATH=1`. Uninstall removes only
the `PATH` entry added by this installer; a pre-existing entry is left intact.

## Terminal UI

`unigetui tui` opens the interactive terminal UI inside the main executable.
On Windows PowerShell, use `.\uniget.exe tui` (or `uniget tui` on PATH): the launcher
forwards to the same command and waits for the session to exit. A direct `.\UniGetUI.exe tui`
launch does not block the PowerShell prompt, which then competes with the TUI for console
input and can cause a startup failure. To launch the main executable itself, use
`Start-Process -FilePath .\UniGetUI.exe -ArgumentList 'tui' -NoNewWindow -Wait`.

Use `uniget tui --help` for its options, or `uniget tui --fake-data` to try it without
calling real package managers. See [TUI.md](TUI.md) for keyboard controls and more options.
This command runs locally, independently of the desktop window or an IPC daemon, and is
included in the same application package; no separate TUI executable is installed.

## Agent Skills

`unigetui skills [args...]` runs the bundled Agent Skills CLI in the current directory without
starting the GUI or connecting to the UniGetUI IPC API. For example, `unigetui skills list`
lists installed agent skills, and `unigetui skills add <source>` installs skills from a source.
On Windows PowerShell, use `.\uniget.exe skills [args...]` to wait for completion and return its exit code.
Skill discovery searches your configured repositories, websites and Notion databases by default.
Enable the optional desktop extension under **Settings > Extensions > Agent Skills & Plugins** to use its
dedicated sidebar page. It is off by default and independent of the bundled CLI. Skills are not
package managers: `package search/install/update/uninstall`, package bundles and the TUI do not
include them.
The optional **Settings > Extensions > Agent Skills & Plugins > Online discovery > Search skills.sh** switch enables
explicit public-catalog searches of at least two characters in **Skills > Discover**. It is off by default;
submitting **Search skills.sh** sends search text to skills.sh. Local filtering and **Refresh sources** do not.
This applies only to discovery on the Agent Skills
extension page; the bundled `skills` CLI does not provide a public-catalog `find` command.
Arguments and exit codes after `skills` belong to that CLI, not to the UniGetUI automation
commands documented below.

The extension's separate **Plugins** section has Browse/Installed views and full-page item details.
Its **Claude Code** provider invokes the installed native CLI to manage whole plugins in user scope.
Choose **Add marketplace** and paste `/plugin marketplace add
Devolutions/ux-prototypes`, then use **Install by identifier** with `/plugin install
ux-design-agents@devolutions-ux` or `/plugin install ux-skills@devolutions-ux` into its
plugin field. These are recognized input formats, not arbitrary shell commands.
The bundled `unigetui skills` CLI still installs individual cross-agent skills; it
does not interpret `/plugin` commands or manage native plugin enablement.
For terminal plugin management use `claude plugin marketplace add <source>` and
`claude plugin install <name>@<marketplace> --scope user` directly. UniGetUI does not
silently install Claude Code or approve marketplace-provided commands.

Select **Codex** in that section to use its native marketplace and plugin commands.
For example:

```shell
codex plugin marketplace add awakecoding/copilot-resources
codex plugin add copilot@copilot-resources
codex plugin marketplace upgrade copilot-resources
codex plugin remove copilot@copilot-resources
```

Codex requires a recent CLI exposing these commands. UniGetUI uses its native executable
(including the Windows binary shipped by npm), not a shell command string. Codex owns
configuration and authentication; enable/disable plugins in Codex itself.

The **Cursor** provider imports complete compatible bundles into Cursor's documented
`~/.cursor/plugins/local` directory using UniGetUI-managed source snapshots. Add
`awakecoding/copilot-resources`, then select `copilot@copilot-resources` to install.
Reload Cursor and check **Customize** afterward. This is local import, not a Cursor
CLI marketplace command or a hosted/team marketplace; Enterprise policy can block it.
Only managed local imports can be updated/uninstalled here, and existing local plugins
are not overwritten. The bundled `unigetui skills` CLI remains independent of these
plugin providers.

The **GitHub Copilot** plugin provider uses the installed native `copilot` executable, not an npm
shell launcher. It loads installed plugins separately, browses one selected marketplace at a time,
and only searches all marketplaces when explicitly requested. Marketplace results are cached for
seven days and reused until you refresh them. It requires the current flat-array JSON inventory
commands (verified with 1.0.105), respects `COPILOT_HOME`, and leaves authentication and policy to
Copilot. For example:

```shell
copilot plugin marketplace add OWNER/REPO
copilot plugin marketplace browse MARKETPLACE --json
copilot plugin install NAME@MARKETPLACE
copilot plugin update NAME@MARKETPLACE
copilot plugin enable NAME@MARKETPLACE
copilot plugin disable NAME@MARKETPLACE
copilot plugin uninstall NAME@MARKETPLACE
```

In the extension, installed plugins load independently from marketplace Browse. Browse one
marketplace on demand or explicitly search all marketplaces; results are cached and paged.
**Update all N** confirms and updates only the shown installed plugins, not the CLI's broader
`plugin update --all` scope. Built-in/external plugins are read-only; marketplace removal does
not use `--force`. GitHub documents using the CLI to install desktop-app extensions, and the
local CLI inventory is the profile shown by the app. Repository settings and managed policy may
change effective enablement; active app sessions may need to reload after changes.

Use **Open in Copilot app** as an optional fallback to open official confirmation forms through
`ghapp://plugins/install?source=...` and `ghapp://plugins/marketplace/add?source=...`, wrapped
in GitHub's HTTPS app launcher. Complete the confirmation in Copilot; opening a form is not
reported as an installation.

The desktop Skills library reuses saved repository/website metadata across restarts.
**Refresh sources** explicitly refetches it; installs and previews still retrieve source content.
**Update all N** applies only the known skill updates shown by the current filters.
These desktop behaviors do not change the bundled `unigetui skills` CLI.

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
- `--manager` uses stable manager ids, not GUI labels. Current ids: `apt`, `aur`, `bun`, `cargo`, `chocolatey`, `dnf`, `dotnet-tool`, `flatpak`, `homebrew`, `npm`, `pacman`, `pip`, `pwsh`, `scoop`, `snap`, `vcpkg`, `winget`, and `winps`.

## Command reference

### Cargo prerequisites and installation behavior

The Cargo manager requires Cargo, not `cargo-binstall` or `cargo-update`. UniGetUI never
installs or invokes those extensions. Existing installations of those tools remain ordinary
Cargo packages and are not removed.

Installed packages are listed with `cargo install --list`, independently of network access.
UniGetUI checks crates.io versions itself, excluding yanked and prerelease updates by default.
Git, local-path, and other-registry packages remain listed with their source identity; they
are not matched against crates.io for updates. Their details, version catalogs, and source
downloads are not substituted with potentially unrelated same-name crates.io packages.
Named-registry and directory-source display labels remain listed even when Cargo does
not provide a source URL; they do not prevent other packages in the same root from loading.

Local, non-elevated installations first attempt a publisher-provided binary for the exact
selected version and Cargo host target. The C# installer supports ZIP, TAR, TAR.GZ/TGZ, and
explicitly configured raw executables. It reads supported publisher manifest metadata or
unambiguous version-specific GitHub release assets. It requires a supported SHA-256/SHA-512
checksum (including GitHub's published asset digest); unsupported signature-only metadata
uses source compilation. No cargo-quickinstall or other third-party build host is used.
Checksums of crates.io source archives are never reused as binary checksums.

When a compatible verified binary is unavailable, the operation log explains the fallback
to `cargo install --locked` for the same version. Custom Cargo/build options and elevated
operations also use Cargo. Compilation still requires a working Rust compiler, linker,
and any crate-specific native dependencies. A checksum mismatch, unsafe archive, or
cancellation is a failure/cancellation, not permission to silently choose another installer.
Integrity skipping and binstall-specific custom arguments are not supported.

Custom installation locations use Cargo's **install-root** semantics: executables go into
`<root>\bin`, with Cargo's tracking files beside that directory. This differs from binstall's
former `--install-path`, which selected the binary directory directly; review saved custom
locations when migrating. Without a custom root, `CARGO_INSTALL_ROOT`, `install.root` in the
Cargo home configuration, `CARGO_HOME`, and the user-default Cargo home determine the root
in that order. Like Cargo itself, the default installed listing does not scan arbitrary
other custom roots.

Uninstallation remains `cargo uninstall`. Command previews, manual-install commands, and
exported install scripts use ordinary Cargo source commands; the managed binary optimization
runs only inside UniGetUI's operation pipeline. Broker-owned operations stay with the Agent:
they never fall back to a local installer after policy denial or broker failure. The Agent's
own Cargo implementation determines its execution prerequisites and binary/source behavior;
client-side helper removal does not change an externally installed Agent.

The standalone NativeAOT interoperability check is
`dotnet publish scripts\CargoSmoke\CargoSmoke.csproj -c Release -r win-x64 --self-contained true /p:Platform=x64`,
followed by `.\scripts\CargoSmoke\bin\x64\Release\net10.0\win-x64\publish\UniGetUI.Cargo.Smoke.exe`.
It uses fixture downloads and an isolated temporary install root, verifies real Cargo
listing/uninstallation, and treats trim/AOT warnings as errors. Use the matching RID on
other hosts; this does not substitute for their native lock-contention tests.

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
| `app navigate` | `--page <page>` | `--manager <id>`, `--help-attachment <path>` | Valid pages include `discover`, `updates`, `installed`, `bundles`, `catalog`, `catalog-editor`, `git-repositories`, `agent-skills`, `xcode` (macOS builds only), `settings`, `managers`, `own-log`, `manager-log`, `operation-history`, `help`, `release-notes`, and `about`. `catalog` and `catalog-editor` require Enable Software Catalog in Experimental settings and developer options, followed by an application restart. `git-repositories` and `agent-skills` require their respective extensions to be turned on in Settings, under Extensions; no restart is needed. `xcode` is included only in macOS builds and requires macOS 13 or later with the Xcode extension turned on the same way. |
| `app quit` | None | None | Gracefully shuts down the selected session, including headless daemons. |

The **Git Repositories** page fetches the remote even when the main clone is on a feature
branch, but only fast-forwards a clean clone checked out on its configured default branch.
It never switches branches automatically. A recognized transient fetch failure is retried
once after a short delay. If refreshing still fails, remote information is marked as
potentially out of date; use **Retry** to check again or **Error details** for the full,
copyable Git output. No Git lock files are removed automatically. The repository list's
**Total size** includes its linked worktrees (hover it for the breakdown); the worktree
list's **Size** is the individual worktree's size. Click a size header to sort largest
first, then smallest first, then back to the original order; **Name** sorts A–Z, then
Z–A. Entries not yet measured sort last. Sizes appear after a background scan and
include build output. They are the sum of file lengths, not allocated disk space;
symbolic links and junctions are not followed.

The **Xcode** page (macOS 13 or later) installs, updates and selects Xcode itself; it needs no
other tool. It ports the relevant parts of [Xcodes.app and xcodes](https://github.com/XcodesOrg)
(MIT License) to C#, including Apple's developer sign-in, and does not read or share their
saved accounts. The version browser combines installed Xcodes (read from their bundles) with the
release list from [Xcode Releases](https://xcodereleases.com). By default it scans `/Applications`
and Xcodes found by Spotlight; when an installation directory is configured, it scans that folder only.
The browser has search, installed-only, release and architecture filters and optional major/minor grouping.
Different builds of the same version have separate available rows; the architecture filter also
selects that architecture when a build offers multiple variants.
Filters, grouping, and expanded or collapsed groups are remembered. Each installed path has its
own details and actions, including **Open**, **Show in Finder**, and **Copy path**. Opening and
revealing use the selected path and report when the system cannot launch them. The active
command-line Xcode is marked separately from the highlighted row. After an Apple session has been
verified, refreshing also reads that account's Apple download list. Apple data is matched to public
release details only by the exact archive address; a version whose build is missing or unrecognized is
shown but cannot be installed or downloaded until its build can be identified. The account-specific list is kept
only in memory and cleared when signing out. If the release list cannot be
read, the last saved list is shown and labeled with its age; if there is none, installed Xcodes
remain visible but available versions and the update badge are withheld. Release details are
matched by version, build and architecture; ambiguous or missing details remain unknown, and
compatibility stays unknown while they are stale. When the catalog reports the same build and
architecture for a release and a pre-release, the details identify the other version; this is
catalog metadata, not a verification of the installed app or downloaded archive. An active
pre-release is marked when the corresponding stable version is installed or available; it is not
selected automatically. Before installing, compatibility is checked against the selected build and
architecture; if either is unknown or its details are stale, compatibility remains unknown. When
that build requires a newer macOS, the install dialog asks for an explicit **Install Anyway** or
**Cancel**. The download made for this Mac (Apple Silicon or
Intel) is preferred when there is one, then the universal one, then an archive whose architecture is
unknown. A specifically requested build and architecture are not substituted.

**Download** saves the selected release's archive to UniGetUI's cache without installing Xcode.
An installed version offers this action only when the catalog contains its exact version, build
and architecture. The action uses the same Apple account, cache, resumable downloads and
available catalog checksums as installation. A `.xip` archive is kept only if macOS reports it as
signed Apple Software under Apple Root CA (`pkgutil --check-signature`); otherwise it is deleted.
An encrypted `.xip.aea` archive, and the Xcode build and app signature inside any archive, are
checked during installation. The success notice can reveal
the cached archive in Finder. Cache cleanup, or **Delete the download once Xcode is unpacked**
after an install, may remove it; copy it elsewhere if it must be retained. The account is checked
again after confirmation, the selected identity is resolved against the cached catalog before
download, and access failures retain the operation log and offer a user-initiated retry.

An install downloads the archive from Apple with the signed-in Apple ID. It resumes a partial
download when the catalog supplies a checksum or Apple provides a strong validator; otherwise it
starts that download again. Archives are cached separately by download address and expected
checksum, and the catalog's checksum is verified when available. It then expands the archive with `xip` (or
`archiveutil` for encrypted archives), checks it with Gatekeeper and its Apple code signature, and
copies it into a newly created installation directory next to the other versions. If the usual
`Xcode-<version>.app` is already a readable Xcode of the same product version but a different
build or architecture, the new copy uses `Xcode-<version>-<build>.app`; an additional architecture
suffix is used when that build path already contains another known variant. Existing paths, including
symbolic links and unrecognized apps, are never replaced. A conflicting destination must be moved
or renamed before retrying. Before running
first-launch commands as administrator, it copies Xcode to a private root-owned folder and checks
the signature, version and exact build again. These copies require additional free space. If the
installation folder requires administrator rights, its path must be on the system volume, owned
by root, free of access-control lists and inaccessible for modification by the requesting user.
The first-launch steps (enabling developer mode, accepting the license and installing its
components) and, when asked, selecting it with `xcode-select` run in one macOS administrator
prompt. Once approved, administrator setup cannot safely be canceled; UniGetUI waits for it to
finish before quitting. An interrupted earlier copy may leave an Xcode folder to inspect or remove
before retrying. Selecting requires a non-symlinked bundle approved by Gatekeeper and signed by
Apple as Xcode; the administrator step protects and rechecks that bundle before calling
`xcode-select`. After installing or selecting, UniGetUI reports when the installed copy or
selected path cannot be confirmed. While a download, install or selection runs, its browser row and details
show progress and a confirmation-protected **Cancel** action; the operation panel keeps a bounded,
redacted log if it fails. Quitting during an install asks first, and stopping it keeps the partial
download for a retry when it can be verified. The archive is kept in the UniGetUI cache folder unless
**Delete the download once Xcode is unpacked** is on. A second UniGetUI process cannot use the same
cached archive until the install finishes.

The account card signs in to Apple with an Apple ID that can download from developer.apple.com,
using Apple's SRP sign-in with two-factor codes from a trusted device or by text message. Apple's
public sign-in configuration is fetched separately, without account cookies or following its sign-out
redirect. The Apple ID, its password and the session are kept in UniGetUI's own login-keychain item, never in
logs or settings, so later downloads do not ask again. **Verify session** checks the session with
Apple, **Use a different Apple ID** signs in with another account, and **Sign out** forgets all
three; installed versions and downloads are kept. Accounts that require a security key, and
federated (managed) Apple IDs, cannot sign in yet. Password and verification dialogs identify the Apple ID being used, and an
install stops if the saved Apple ID changed while its confirmation was open. When Apple refuses
access (HTTP 401 or 403), the failed-operation panel keeps the original log and offers **Use a
different Apple ID**, the Apple Developer account page (where new agreements may need to be
accepted), and an explicit retry; a 401 or 403 does not by itself identify the cause.
UniGetUI also rechecks its saved account before validating a session or starting an authenticated
catalog request or download, and after validation. If it detects an external change, it stops and asks
you to refresh the account. Another process can still change the Keychain while a request is in
flight; UniGetUI checks again before saving that request's cookies.

UniGetUI does not delete Xcode itself: **Uninstall...** shows the selected copy in Finder so you
can move it to the Trash yourself. After an update, the shortcut to the older copy appears only
when its path, version, build, and architecture still match. An update is the newest stable
release when it is newer than every installed Xcode (a pre-release counts when **Include
pre-releases** is on); nothing is offered while no Xcode is installed, and the older version is
kept until it is uninstalled. **Install Xcode updates automatically** is off by default. When
enabled along with a periodic check, it installs only a freshly checked update with a known build,
architecture and supported macOS compatibility, and only while the saved Apple session is valid.
It pauses for manual selection if that version has multiple builds or an unverified build identity;
different architectures of the same known build do not by themselves prevent automatic installation.
It does not prompt for Apple sign-in automatically; verify or sign in again to resume. The macOS
administrator prompt may still appear. A failed or cancelled automatic attempt is not retried
again in the same app session; you can retry it manually.

**Installation directory** accepts an existing absolute folder, or an empty value to use
`/Applications`. It applies to listing,
update checks, selection, and installations; changing it changes which installed Xcodes the page
can see. It cannot be changed while an Xcode operation or account action is running.

**Archive directory** accepts an existing absolute folder, or an empty value to keep downloaded
Xcode archives in UniGetUI's cache folder. Temporary expansion folders during installation are
created beside the cached archive in that directory as well. Existing cached archives in a
previous folder are not moved automatically, and an inaccessible custom folder fails before Apple
sign-in rather than falling back to the default cache. It cannot be changed while an Xcode
operation or account action is running.

**Simulator runtimes** lists Apple's public simulator catalog separately from the installed and
bundled runtimes reported by `xcrun simctl runtime list -j`. The local list belongs to the developer
tools selected by `xcrun`, which `DEVELOPER_DIR` may override, not necessarily the Xcode highlighted
in the version browser. Platform, source, release-stage, and architecture filters plus search narrow
the list. The filters are remembered separately from the version browser; search text is not saved.

**View mapped simulator runtimes** in a version's details narrows it to Apple catalog mappings for
that Xcode's SDK platform, version, and build. Only SDK builds shared by every possible Xcode
variant are used; installed or bundled rows also need an exact platform, version, build, and known
architecture match to a mapped catalog entry. Missing or stale metadata is identified, and a
missing mapping does not mean a runtime is incompatible. This view does not change the selected
developer tools: install and export still use the tools currently selected by `xcrun`.

Unknown release stages and architectures appear only when their respective filters are set to
**All**. The last successful Apple simulator catalog is saved across restarts and marked stale until
refreshed; it can be browsed offline but cannot authorize installs, exports, or downloads. The local
runtime list is retained for the session and marked potentially out of date if its next refresh fails.
The catalog entry being installed, exported, or downloaded shows progress and a confirmation-protected
**Cancel** action; the global operation panel remains available if a filter hides that entry. You can
return to the runtime browser during an operation without refreshing its catalog.
For installed or bundled runtimes with a reported absolute local path, **Show in Finder** reveals the
runtime folder or image and **Copy path** copies that path. Finder reports when the item no longer exists.
For supported modern Apple catalog entries,
**Install with Xcode** asks Xcode to download and install the selected build and architecture.
This needs Xcode 16.1 or later with explicit architecture selection, or Xcode 26 or later for an
Apple Silicon runtime; the dialog identifies the developer tools that will be used. Xcode manages
its own Apple account and may ask for administrator authorization; the Apple ID UniGetUI downloads Xcode
with is not used.
The installed build and architecture must be reported ready by `simctl` before UniGetUI reports
success. **Export with Xcode** asks the same selected Xcode for the specified platform, build and
architecture and saves an image in a folder you choose. Xcode may also install the runtime on this
Mac. The image is first written to a private system temporary directory and then copied and checked
before publication under a new name; existing files are not overwritten. Allow enough space in both
locations. Xcode determines the image contents: UniGetUI verifies the copy, but does not
independently verify the exported image's build or Apple signature. Cancelling requests that
`xcodebuild` stop, but macOS or Xcode may finish system-managed work afterwards. Temporary data or
an incomplete `.partial` copy may remain after cancellation or failure, and their paths are shown
in the notice.

For legacy simulator packages that Apple's catalog explicitly hosts on its public
`devimages-cdn.apple.com` server, **Download from Apple** saves the image without using an Apple
account or installing it. A fresh catalog entry and confirmation are required; authenticated
sources and redirects are not used. UniGetUI checks that the saved bytes match the response but
does not independently verify the image's build, Apple signature, or compatibility with the
selected Xcode. The catalog size is approximate. Downloads are capped at 20 GiB, do not overwrite
existing files, and cannot be resumed; an incomplete `.partial` file may remain after cancellation
or failure and its path is shown. Other unsupported or unknown runtime types remain read-only.
The page does not install legacy images or remove runtimes.

The account card appears on the Xcode page and under **Settings > Extensions > Xcode**; both are
disabled while an Xcode operation is running.

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
| `manager list` | None | None | Lists managers, their automation-relevant capability flags and their listing health (see below). |
| `manager maintenance` | `--manager <id>` | None | Returns maintenance metadata for one manager, including the actions it supports in `supportedActions` and its listing health. |
| `manager reload` | `--manager <id>` | None | Reloads one manager. |
| `manager set-executable` | `--manager <id>`, `--path <path>` | None | Sets a custom executable override, then reloads the manager. |
| `manager clear-executable` | `--manager <id>` | None | Clears the custom executable override, then reloads the manager. |
| `manager action` | `--manager <id>`, `--action <action>` | `--confirm` | Runs one of the actions the manager lists in `supportedActions`. Every action except `reload` requires `--confirm`. |
| `manager enable` | `--manager <id>` | None | Enables the manager. |
| `manager disable` | `--manager <id>` | None | Disables the manager. |
| `manager notifications enable` | `--manager <id>` | None | Enables update notifications for the manager. |
| `manager notifications disable` | `--manager <id>` | None | Disables update notifications for the manager. |

Maintenance actions, as `manager action --action` accepts them:

| Action | Managers | Notes |
| --- | --- | --- |
| `reload` | All | Looks for the manager again and reloads its configuration. |
| `stop-stuck-listings` | All | Kills the processes of any search, package listing or maintenance command the manager is still running. |
| `repair-winget` | WinGet (Windows) | Clears WinGet's temp folder, resets its sources, repairs it through `Microsoft.WinGet.Client` and resets App Installer. Elevated. |
| `reset-sources` | WinGet (Windows) | Runs `winget source reset --force`. Elevated. |
| `install-scoop`, `uninstall-scoop` | Scoop (Windows) | Installs Scoop (elevated), or removes it with every package it installed. |
| `cleanup-scoop` | Scoop (Windows) | Removes old app versions, per user and machine-wide (the machine-wide half is elevated), then empties Scoop's download cache, which both scopes share. |
| `clear-cache` | Chocolatey, Pip, Npm, Bun, .NET Tool, Homebrew | Runs the manager's own cache-cleaning command. |

Actions that need the manager's own executable are only listed once the manager has been found. `repair-winget`, `reset-sources` and `install-scoop` are listed even when it has not. A maintenance command that runs for more than 15 minutes is stopped and reported as failed.

#### Manager health

`manager list` and `manager maintenance` include a `health` object built from a record UniGetUI keeps
for each manager across restarts, in `ManagerHealth.json` in its data folder:

| Field | Meaning |
| --- | --- |
| `state` | `loading` (still starting), `unknown` (nothing listed yet), `healthy`, `failing`, `malfunctioning`, `not-found` or `disabled`. |
| `needsAttention` | `true` when the app shows its health banner: a malfunction, or a listing that failed at least twice in a row with its last failure in the past 24 hours. For WinGet, Apt, Dnf and Pacman a single failed installed listing is enough. |
| `failingListing` | `search`, `installed` or `updates`: the listing that failed most recently, or `installed` for a malfunction. |
| `reason`, `consecutiveFailures` | The failing listing's last error and how many times in a row it failed. |
| `search`, `installed`, `updates` | Per listing: `lastSuccessUtc`, `lastFailureUtc`, `lastFailureReason`, `lastFailureWasTimeout`, `consecutiveFailures`, `lastPackageCount`, `lastFastRepairUtc`, `lastFastRepairReason` (a first attempt that failed and was retried) and `recentDurationsMs` (the last ten). |

A manager is `malfunctioning` when it is ready, its installed listing succeeded and returned no package
at all, and a working installation of it always lists something (WinGet, Apt, Dnf and Pacman). The
`DisableWinGetMalfunctionDetector` setting still turns this off for WinGet.

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
| `package update-all` | None | `--manager <id>`, `--wait true\|false`, `--timeout <seconds>`, `--delay <seconds>` | Queues an update for every available update and returns their operation ids. With `--wait true`, waits for all of them (default timeout 3600 seconds) and exits with `1` if any did not succeed or the wait timed out. Works in headless sessions. |
| `package update-manager` | `--manager <id>` | `--wait true\|false`, `--timeout <seconds>`, `--delay <seconds>` | Same as `package update-all --manager <id>`. |

## Headless behavior

When UniGetUI is started with `--headless`, it exposes the same automation API without opening a window.

| Command | Headless behavior |
| --- | --- |
| `status`, `app status`, `app quit` | Fully supported. |
| `app show` | Fails with “the current UniGetUI session is running headless and has no window to show.” |
| `app navigate` | Fails with “the current UniGetUI session is running headless and cannot navigate UI pages.” |
| `package show` | UI-oriented; may fail or be meaningless in pure headless sessions. |
| `package update-all`, `package update-manager` | Fully supported. The headless session queues the updates itself. |

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
| `--register-autostart` | Registers UniGetUI to start minimized at login for the current user, then quits. | Windows. Run once per user at logon by per-machine installs; the on/off toggle in Startup Apps is kept. |
| `--help` / `-h` | Prints CLI help. | For the direct verb-based CLI. |
| `--import-settings <file>` | Imports settings from a JSON file. | Existing settings are replaced. |
| `--export-settings <file>` | Exports settings to a JSON file. | Creates or overwrites the file. |
| `--enable-setting <key>` / `--disable-setting <key>` | Toggles one boolean setting. | Legacy setting flags. |
| `--set-setting-value <key> <value>` | Sets one string-backed setting. | Legacy setting flag. |
| `--no-corrupt-dialog` | Shows the verbose crash report instead of the simplified dialog. | Troubleshooting flag. |
| `--enable-secure-setting <key>` / `--disable-secure-setting <key>` | Toggles one secure setting for the current user. | May require elevation. |
| `--enable-secure-setting-for-user <user> <key>` / `--disable-secure-setting-for-user <user> <key>` | Toggles one secure setting for a specified user. | May require elevation. |
| `--delete-shortcuts <path> [<path>...]` | Deletes desktop or Start Menu shortcuts, then quits. | Used by UniGetUI to remove shortcuts the current user is not allowed to delete; refuses any path that is not a `.lnk` or `.url` under a desktop or Start Menu folder. |
| `<bundle-file>` | Loads a valid bundle file into the Package Bundles page. | Supported extensions include `.ubundle`, `.json`, `.yaml`, and `.xml`. |
| `pkg-helper install-daemon` / `remove-daemon` | Installs (or removes) the root service that lets standard users install catalog packages without an administrator password. | macOS, as root. `pkg-helper serve` is what the service runs. See [PKG_FEEDS.md](PKG_FEEDS.md). |
| `pkg-index <folder>` | Writes the `index.json` that publishes a folder of packages as a web feed, then quits: `.exe` and `.msi` files on Windows, `.pkg`, `.dmg` and `.zip` files elsewhere. Entries another platform wrote are kept while their files are unchanged. | `.dmg` files are read on macOS only. Exits 3 when a file in the folder could not be read. See [PKG_FEEDS.md](PKG_FEEDS.md) and [WINDOWS_INSTALLER_FEEDS.md](WINDOWS_INSTALLER_FEEDS.md). |

## Other environment variables

These are read by the application itself rather than by the CLI client.

| Variable | Values | Meaning |
| --- | --- | --- |
| `UNIGETUI_WINGET_CLI` | `default`, `winget`, `pinget` | Chooses which WinGet command-line tool the WinGet manager drives. Takes precedence over the `WinGetCliToolPreference` setting. |
| `UNIGETUI_WINGET_COM` | `default`, `enabled`/`enable`/`on`/`true`/`1`, `disabled`/`disable`/`off`/`false`/`0` | Forces the WinGet COM API on or off instead of letting UniGetUI decide. Takes precedence over the `WinGetComApiPolicy` setting. |
| `UNIGETUI_FONT_FAMILY` | A font family name | Prepends a family to the UI font chain, ahead of the platform default (Segoe UI on Windows, the bundled Inter on Linux, the system font on macOS). Ignored when the "use the system UI font" setting is on, which itself does not apply on macOS. An entry containing the Avalonia `$Default` family is discarded. |
| `UNIGETUI_FORCE_NATIVE_LINUX_DECORATIONS` | `1`/`true`/`on`/`yes`/`enabled`, `0`/`false`/`off`/`no`/`disabled` | Linux only. Forces the window manager's own title bar on or off instead of auto-detecting. An unrecognized value is ignored with a warning. |
| `UNIGETUI_GITHUB_TOKEN_NAMESPACE` | Any string | Suffixes the credential-store entry holding the GitHub backup token, so several UniGetUI instances on one machine can hold separate logins. |
| `WEBVIEW2_BROWSER_EXECUTABLE_FOLDER` | A directory path | Windows only. Points the embedded web view at a fixed-version WebView2 runtime instead of the installed evergreen one. |

## Deep links

The Windows installer registers a `unigetui://` protocol handler for regular installations; a portable install does not register it, see [PORTABLE.md](PORTABLE.md).

### Package links

A package link opens a package's details in UniGetUI, from where the user can install it:

```
unigetui://package?manager=winget&source=winget&id=TechSmith.Snagit.2025&name=Snagit%202025
```

Right-click a package on the Discover Packages or Installed Packages page and choose **Copy link** to put a link to it on the clipboard.

| Parameter | Required | Meaning |
| --- | --- | --- |
| `id` | Yes | The package identifier. It must match exactly, ignoring case. |
| `manager` | No | The package manager's identifier, as `unigetui manager list` prints it (`winget`, `chocolatey`, `scoop`, `npm`, ...). Without it, every enabled package manager is searched. |
| `source` | No | The source name, for package managers with several sources (`winget`, `msstore`, `community`, ...). Without it, any source matches. |
| `version` | No | The version to install instead of the latest one. |
| `name` | No | The package's name. It is only shown while UniGetUI looks for the package and if the package is not found; it is never used to find the package. |

Values are percent-encoded. Unknown parameters are ignored. A link is refused if it has no `id`, repeats a parameter, contains a control character, has a value longer than 256 characters, or is longer than 2048 characters.

A link only names a package. Opening one never installs anything by itself: it shows the package details, and the package is installed only if the user clicks Install. The installation uses the user's own install options for that package, and no installer address, argument or other install option can be passed in a link. A package that is only known to this computer, such as a WinGet "Local PC" entry, cannot be linked to.

The link works whether or not UniGetUI is already running. If the package manager is disabled or not available, or the package is not found, UniGetUI says so and opens nothing.

### Notification actions

These links route notification clicks back into a running instance:

| Deep link | Meaning |
| --- | --- |
| `unigetui://openUniGetUI` | Shows UniGetUI and brings the window to the front. |
| `unigetui://openUniGetUIOnUpdatesTab` | Shows UniGetUI on the Software Updates page. |
| `unigetui://updateAll` | Starts an update for every available package update. |
| `unigetui://releaseSelfUpdateLock` | Allows a pending UniGetUI self-update to proceed. |

Anything else after `unigetui://` is ignored. A notification action is only dispatched when UniGetUI is already running: a link that cold-starts the app launches it normally and the action is dropped. To drive UniGetUI programmatically, use the verb commands above or the [IPC API](IPC.md) rather than deep links.

## Installer parameters

The installer is Inno Setup based. It supports the standard [Inno Setup command-line parameters](https://jrsoftware.org/ishelp/index.php?topic=setupcmdline) plus these UniGetUI-specific switches:

| Parameter | Meaning |
| --- | --- |
| `/NoAutoStart` | Do not launch UniGetUI after installation. |
| `/NoRunOnStartup` | Do not register UniGetUI to start minimized at login. On a per-machine install this applies to every user: an earlier all-users registration is removed, and each user's entry from it is deleted at their next logon. |
| `/NoVCRedist` | Skip installation of the MSVC x64 runtime. |
| `/NoEdgeWebView` | Skip installation of the Microsoft Edge WebView runtime. |
| `/NoChocolatey` | Deprecated no-op kept for compatibility. |
| `/EnableSystemChocolatey` | Deprecated no-op kept for compatibility. |
| `/NoWinGet` | Do not install WinGet and Microsoft.WinGet.Client if they are missing. |
| `/MSStore` | Microsoft Store install mode: skip the MSVC and WebView2 dependency installers, do not launch UniGetUI after installation, and disable startup at login. Use with `/CURRENTUSER` to select user-local scope. |

The installation type is an Inno Setup task rather than a switch. Pass `/TASKS="portableinstall"` for a portable installation; the default is `regularinstall`, which additionally accepts `regularinstall\startmenuicon` and `regularinstall\desktopicon`. A portable install keeps its settings beside the executable and registers no protocol handler, file association, shortcuts or startup entry, see [PORTABLE.md](PORTABLE.md).
