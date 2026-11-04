# Setting up package managers

UniGetUI is a front end: it drives package managers that are already installed on the computer,
and apart from Scoop it does not install them for you. This page explains, for each one, what to
install, how UniGetUI finds it, and how to check that it works.

Each manager has its own page, opened from **Package Managers** in the sidebar or from
**Settings > Package manager preferences**. When a manager is not ready, the status card at the top
of that page links to its section of this guide. When the manager is also not installed, the card
shows the install command from its section, where there is a one-line one (Scoop has its own
**Install Scoop** button instead), with **Open in a terminal**: that copies the command to the
clipboard and opens a terminal with it already typed (Windows PowerShell on Windows, Terminal on
macOS, the first terminal emulator UniGetUI finds on Linux).

## Before you start

- **A manager that is missing the first time UniGetUI checks for it is turned off.** After
  installing it, open its page and turn on **Enable {manager}**: UniGetUI looks for it again
  straight away. A manager that was found before and has since been removed stays on, and its page
  reports that it was not found.
- **PATH on Windows.** UniGetUI finds most managers by searching `PATH`. On Windows it reads the
  user and system `PATH` from the registry each time it searches (user entries first), so a manager
  installed while UniGetUI is open is found when you turn on **Enable {manager}**, without a restart.
- **PATH on macOS.** At startup, UniGetUI replaces its `PATH` with the one a zsh login shell reports
  (`zsh -l -c "printenv PATH"`). That shell reads `~/.zshenv`, `~/.zprofile` and `~/.zlogin`, but not
  `~/.zshrc`, so a folder added to `PATH` only in `~/.zshrc`, or only in another shell's profile, is
  not seen. If the shell does not answer within 5 seconds, UniGetUI keeps the `PATH` it was started
  with. Restart UniGetUI after installing a manager.
- **PATH on Linux.** UniGetUI uses the `PATH` it was started with. Restart UniGetUI after installing
  a manager.
- **Extra folders.** For some managers UniGetUI also searches fixed folders that are often missing
  from `PATH`; each section below lists them.
- **Install commands.** The Windows commands below use WinGet, and most macOS ones use Homebrew.
  UniGetUI can run without the system WinGet (it falls back to its bundled Pinget), so `winget`
  may be missing even though UniGetUI works; see [WinGet](#winget) to get it back, or use the
  direct download linked beside each command. Without Homebrew, see [Homebrew](#homebrew) or the same
  links. The status card only offers a command when it can find the program that runs it.
- **Picking the executable yourself.** Each manager's page lists the executables it found. Choosing
  another one, or browsing to a file, is disabled by default for security reasons. To allow it,
  turn on **Allow changing the paths for package manager executables** under **Settings >
  Administrator rights and other dangerous settings**.
- **When a manager still is not detected.** Each section ends with a command to run in a terminal.
  If that command works but UniGetUI still reports the manager as missing, open **More > UniGetUI
  Log** in the sidebar: it records whether each manager was found and the errors raised while
  starting it. **View {manager} logs**, on the manager's page, shows the commands UniGetUI ran with
  that manager and their output.

| Manager | Windows | macOS | Linux |
|---|---|---|---|
| [WinGet](#winget) | Yes | | |
| [Scoop](#scoop) | Yes | | |
| [Chocolatey](#chocolatey) | Yes | | |
| [PowerShell 5.x](#powershell-5x) | Yes | | |
| [PowerShell 7.x](#powershell-7x) | Yes | Yes | Yes |
| [Pip](#pip) | Yes | Yes | Yes |
| [Npm](#npm) | Yes | Yes | Yes |
| [Bun](#bun) | Yes | Yes | Yes |
| [.NET Tool](#net-tool) | Yes | Yes | Yes |
| [Cargo](#cargo) | Yes | Yes | Yes |
| [vcpkg](#vcpkg) | Yes | Yes | Yes |
| [Homebrew](#homebrew) | | Yes | Yes |
| [Apt](#apt), [Dnf](#dnf), [Pacman](#pacman), [Snap](#snap), [Flatpak](#flatpak) | | | [Depends on the distribution](#linux-distributions) |

Installer packages published by your organization (`.msi`, `.exe`, `.pkg`, `.dmg`) need no package
manager: see [WINDOWS_INSTALLER_FEEDS.md](WINDOWS_INSTALLER_FEEDS.md) and [PKG_FEEDS.md](PKG_FEEDS.md).

## WinGet

**Nothing to install.** WinGet is part of App Installer, which Windows 11 and current versions of
Windows 10 include and which the Microsoft Store keeps up to date. UniGetUI uses the system WinGet
when it is found and works; otherwise it uses Pinget, a WinGet client that ships with UniGetUI. On
the WinGet page, **WinGet command-line tool** chooses between them.

If WinGet lists no packages, try **Reset WinGet** on its page. To repair the system copy, update
**App Installer** from the Microsoft Store.

Check: `winget --version`

## Scoop

On the Scoop page, turn on **Enable Scoop**, then use **Install Scoop**. It sets the PowerShell
execution policy to `RemoteSigned` for your user, installs Scoop, `scoop-search`, and Git if Git is
missing, then restarts UniGetUI.

To install Scoop yourself instead, run this in a PowerShell window that is **not** elevated (Scoop's
installer refuses to run as administrator unless told to):

```powershell
Invoke-RestMethod -Uri https://get.scoop.sh | Invoke-Expression
```

UniGetUI also needs `scoop-search` and Git. When Scoop is found but either one is missing, UniGetUI
offers to install it when it starts, with `scoop install main/scoop-search` or
`scoop install main/git`, unless you chose not to be asked again.

UniGetUI finds `scoop.ps1` on `PATH` and runs it with PowerShell 7 when `pwsh.exe` is on `PATH`,
or with Windows PowerShell otherwise.

Check: `scoop --version`

## Chocolatey

UniGetUI no longer ships a copy of Chocolatey; install it once:

```powershell
winget install --exact --id Chocolatey.Chocolatey
```

Without WinGet, follow <https://chocolatey.org/install>.

UniGetUI looks for `C:\ProgramData\chocolatey\bin\choco.exe` first, then `choco.exe` on `PATH`.

Check: `choco --version`

## PowerShell 5.x

**Nothing to install.** UniGetUI uses Windows PowerShell (`powershell.exe` on `PATH`, else
`%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe`) and manages modules from the
PowerShell Gallery through PowerShellGet (`Install-Module`, `Get-InstalledModule`).

Windows PowerShell ships with version 1.0.0.1 of PowerShellGet, which Microsoft no longer supports;
to update it, follow
<https://learn.microsoft.com/powershell/gallery/powershellget/install-powershellget>.

Check: run `$PSVersionTable.PSVersion` in a Windows PowerShell window.

## PowerShell 7.x

Install PowerShell 7:

- **Windows:** `winget install --exact --id Microsoft.PowerShell`, or follow
  <https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows>
- **macOS:** `brew install powershell`, or follow
  <https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-macos>
- **Linux:** follow Microsoft's instructions for your distribution:
  <https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-linux>

UniGetUI finds `pwsh` on `PATH` and manages modules through PSResourceGet (`Install-PSResource`,
`Get-InstalledPSResource`), which PowerShell 7.4 and later include.

Check: `pwsh -NoProfile -Version`

## Pip

Install Python, which includes pip:

- **Windows:** `winget install --exact --id Python.Python.3.14` (this installer adds Python to
  `PATH`), or download it from <https://www.python.org/downloads/windows/>
- **macOS:** `brew install python`, or download it from <https://www.python.org/downloads/macos/>
- **Linux:** use your distribution's packages, for example `python3-pip` on Ubuntu.

UniGetUI always runs `python -m pip` with the Python it selects. On Windows it looks for `python`
on `PATH`, then for Pythons installed in Program Files, `%LOCALAPPDATA%\Programs\Python` and the
Python install manager's `%LOCALAPPDATA%\Python`. On macOS and Linux it looks for `python3`, then
`python`, on `PATH`. If typing `python` in a Windows terminal opens the Microsoft Store or reports
that the command is not found, UniGetUI ranks that alias below any installed Python. Python's
documentation covers this case: open Start, search for **Manage app execution aliases**, and check
the Python entries there (<https://docs.python.org/3/using/windows.html>).

Virtual environments, conda environments and embedded Pythons usually belong to another app, so
UniGetUI does not manage them unless you check them under **Python environments** in the Pip
settings. Checking one that is not managed by default requires the administrator setting that
allows changing package manager executables.

Check: `python -m pip --version` (or `python3 -m pip --version`)

## Npm

Install Node.js, which includes npm:

- **Windows:** `winget install --exact --id OpenJS.NodeJS.LTS`
- **macOS:** `brew install node`
- **Windows or macOS without WinGet or Homebrew:** download it from <https://nodejs.org/en/download>
- **Linux:** use your distribution's packages, or [nvm](https://github.com/nvm-sh/nvm).

On Windows, UniGetUI finds `npm.cmd` on `PATH` and runs npm through Windows PowerShell.

On macOS and Linux, UniGetUI uses `npm` on `PATH` first. It also finds npm versions installed by nvm
(under `$NVM_DIR/versions/node/`, or `~/.nvm/versions/node/` when `NVM_DIR` is not set), so they
work even when nvm only sets up `PATH` in `~/.zshrc`. Among those, it prefers nvm's `default`
version, then the newest one.

Check: `npm --version`

## Bun

- **Windows:** `winget install --exact --id Oven-sh.Bun`
- **macOS:** `brew install oven-sh/bun/bun`
- **Linux, or without WinGet or Homebrew:** follow <https://bun.sh/docs/installation>

UniGetUI finds `bun` on `PATH` (on Windows, `bun.exe`, or the `bun.cmd` wrapper). It searches no
other folder, so the folder Bun is installed in must be on `PATH`.

Check: `bun --version`

## .NET Tool

.NET tools are a feature of the .NET **SDK**, so install the SDK:

- **Windows:** `winget install --exact --id Microsoft.DotNet.SDK.10`
- **macOS:** `brew install --cask dotnet-sdk`
- **Windows or macOS without WinGet or Homebrew:** download the SDK from
  <https://dotnet.microsoft.com/download>
- **Linux:** follow <https://learn.microsoft.com/dotnet/core/install/linux>

UniGetUI finds `dotnet` on `PATH`, and treats .NET Tool as missing when `dotnet tool -h` exits with
an error.

Check: `dotnet tool list --global`

## Cargo

Install Rust through rustup:

- **Windows:** `winget install --exact --id Rustlang.Rustup`
- **macOS, Linux, or Windows without WinGet:** follow <https://rustup.rs>

No Cargo extensions are needed. Older versions of UniGetUI also required `cargo-update` and
`cargo-binstall`; current versions search, install and update crates without them.

When UniGetUI installs or updates a crate, it downloads a verified prebuilt binary when the crate
publishes one for your platform. Otherwise it builds the crate with `cargo install --locked`, and it
always builds when the operation runs as administrator, uses custom Cargo arguments or build
settings, or comes from a source other than crates.io. Building needs a working linker and any
native libraries the crate uses; on Windows, Rust's default toolchain gets its linker from Visual
Studio or the Visual Studio Build Tools
(<https://rust-lang.github.io/rustup/installation/windows-msvc.html>).

UniGetUI finds `cargo` on `PATH`, and also in `$CARGO_HOME/bin` (or `~/.cargo/bin` when
`CARGO_HOME` is not set), so it works before a new terminal or session has picked up rustup's
`PATH` change.

Check: `cargo --version`

## vcpkg

vcpkg is set up by cloning its repository and running its bootstrap script once:

```shell
git clone https://github.com/microsoft/vcpkg
```

Then run `bootstrap-vcpkg.bat` (Windows) or `bootstrap-vcpkg.sh` (macOS, Linux) inside the clone.
After that, UniGetUI re-runs the bootstrap script itself each time it loads vcpkg.

UniGetUI needs to know where that folder is. It uses the first of these that is set, and does not
move on to the next one when a value is wrong:

1. The folder chosen with **Change vcpkg root location** on the vcpkg page.
2. The `VCPKG_ROOT` environment variable.
3. A `vcpkg` on `PATH` whose folder contains a `triplets` folder.

UniGetUI also needs Git to refresh vcpkg's package list. When vcpkg is set up but Git is not on
`PATH`, UniGetUI offers to install Git when it starts, unless you chose not to be asked again: with
WinGet on Windows, with Homebrew (or the Xcode command-line tools when Homebrew is absent) on macOS,
and on Linux with the first of `apt-get`, `dnf`, `pacman` or `zypper` it finds.

Check: `vcpkg version`

## Homebrew

Install it from <https://brew.sh>:

```shell
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
```

UniGetUI finds `brew` on `PATH`, and also in `/opt/homebrew/bin`, `/usr/local/bin` and
`/home/linuxbrew/.linuxbrew/bin`. UniGetUI offers casks on macOS only.

Check: `brew --version`

## Linux distributions

UniGetUI decides which Linux package managers to offer from the `ID` and `ID_LIKE` lines of
`/etc/os-release`, so a derivative counts as the distribution it names there. If the file cannot be
read, all five are offered.

| Manager | Offered when `ID` or `ID_LIKE` contains |
|---|---|
| Apt | `debian` or `ubuntu` |
| Dnf | `fedora`, `rhel` or `centos` |
| Pacman | `arch` |
| Snap, Flatpak | `ubuntu`, `debian`, `fedora` or `arch` |

## Apt

Apt is the package manager of Debian, Ubuntu and their derivatives; there is nothing to install.
UniGetUI finds `apt` on `PATH`, in `/usr/bin` or in `/usr/local/bin`.

Check: `apt --version`

## Dnf

Dnf is the package manager of Fedora, RHEL and CentOS; there is nothing to install. UniGetUI looks
for `dnf5` on `PATH` first, then `dnf` on `PATH`, then in `/usr/bin` and `/usr/local/bin`.

Check: `dnf --version`

## Pacman

Pacman is the package manager of Arch Linux and its derivatives; there is nothing to install.
UniGetUI finds `pacman` on `PATH`, in `/usr/bin` or in `/usr/local/bin`.

Check: `pacman -Q pacman`

## Snap

If `snap` is not available, install `snapd` following <https://snapcraft.io/docs/installing-snapd>.
UniGetUI finds `snap` on `PATH`, in `/usr/bin` or in `/usr/local/bin`.

Check: `snap --version`

## Flatpak

Install `flatpak` and add Flathub following <https://flathub.org/setup>. UniGetUI finds `flatpak` on
`PATH`, in `/usr/bin` or in `/usr/local/bin`.

Check: `flatpak --version`
