# Diagnosing Package & Manager Issues in UniGetUI

Before opening an issue for a failed package installation, update, or uninstall, verify whether the failure originates in the package manifest/installer, the underlying package manager CLI, or UniGetUI's command construction and backend selection.

## 1. Compare the Exact Command Line Executed by UniGetUI

A bare terminal command such as `winget update Foo.Bar` is often **not** equivalent to what UniGetUI ran. Open **Package Manager Logs** (bottom-left corner of UniGetUI) and copy the exact `Executable:` and `Arguments:` lines. Check for:

- **Source pin (`--source`):** UniGetUI pins the package source (for example `--source winget` or `--source msstore`). Running `winget` without `--source` may resolve a different source.
- **Scope (`--scope user` vs `--scope machine`) and Elevation:** UniGetUI may run elevated (Administrator / `gsudo`) or pass explicit scope flags that differ from a standard user shell.
- **Custom install location (`--location`):** If a default install location is configured in UniGetUI settings or per-package options, UniGetUI appends `--location "<path>"`. Some installers (including certain `burn` or MSI installers) reject custom location arguments.
- **WinGet Backend Mode:** UniGetUI can query WinGet through the native **WinGet COM API**, the **system `winget.exe` CLI**, or the **bundled WinGet (`winget-cli_x64\winget.exe` / Pinget)** fallback. When testing in a terminal, run the exact executable shown in the operation log.

If the **exact** command line shown in UniGetUI's log fails with the same error in an external terminal (for example, installer hash mismatch, broken download URL, 404/403 from upstream CDN, or installer exit code `1603`), the issue is typically in the upstream package manifest or package manager rather than UniGetUI.

## 2. Safe Configuration Troubleshooting (Do Not Delete User Data)

Never delete `%LOCALAPPDATA%\UniGetUI` (`%UserProfile%\AppData\Local\UniGetUI`) as a first troubleshooting step—doing so destroys saved settings, ignored updates, and local state.

If you need to test whether a corrupted setting or cache file causes a startup crash:

1. Close UniGetUI completely.
2. Rename or back up the configuration directory temporarily (for example, rename `UniGetUI` to `UniGetUI.backup`), or export your settings first.
3. Restore the backup after testing and attach the crash report or exception stack trace to your GitHub issue.

## 3. Collecting Logs Across Platforms

- **UniGetUI Application Log:** Click the log button in the bottom-left status bar and select **UniGetUI Log**.
- **Package Manager Log:** Click the log button in the bottom-left status bar and select **Package Manager logs**, then choose the affected manager.
- **Startup or Hard Crashes:** Include the OS crash report, Windows Event Viewer `.NET Runtime` exception stack trace, or terminal stdout/stderr output when launching `unigetui` from a shell on Windows, Linux, or macOS.
