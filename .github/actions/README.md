# Live desktop actions

Composite actions for workflows that build and run UniGetUI in a real interactive Windows
desktop on a `windows-latest` runner. The desktop is a localhost RDP session driven by
`ironrdp-agent` from [IronRDP](https://github.com/Devolutions/IronRDP), built from a pinned
commit and cached.

The desktop exists only on the runner that set it up, so everything that drives it runs as
later steps of the same job. That is why these are composite actions and not a reusable
workflow.

| Action | What it does |
|---|---|
| [`desktop-session`](desktop-session/action.yml) | Generic. Fetches IronRDP into `RUNNER_TEMP`, restores or builds `ironrdp-agent`, enables local RDP, connects at `desktop-size`, and starts a PowerShell remoting endpoint *inside* the desktop session. Exports `DESKTOP_SESSION_*` variables. Nothing in it is UniGetUI-specific, and it leaves the workspace untouched. |
| [`desktop-session-cleanup`](desktop-session-cleanup/action.yml) | Disconnects and restores the runner's RDP settings, and optionally uploads the artifacts directory (`artifact-name`). Composite actions have no post step, so run it with `if: always()`. |
| [`unigetui-build`](unigetui-build/action.yml) | Builds an unsigned installer from the checked-out source with `scripts/build.ps1`. Output: `installer-path`. |
| [`unigetui-desktop`](unigetui-desktop/action.yml) | Installs UniGetUI (latest release, a `release` tag, or `installer-path`), disables telemetry and the Cargo manager, launches it on the desktop, skips the first-run dependency dialogs, and leaves the main window maximized in the foreground. Outputs: `exe-path`, `process-id`, `window-handle`, `baseline-screenshot`. |

Two workflows use them:

- [`desktop-smoke.yml`](../workflows/desktop-smoke.yml) is the reference example: install
  UniGetUI, check that its window is on screen, clean up.
- [`agent-implement-ticket.yml`](../workflows/agent-implement-ticket.yml) has a GitHub
  Copilot CLI agent implement an issue (or ticket text) on the live desktop. The agent
  follows [`PROMPT.md`](../agent/implement-ticket/PROMPT.md), builds and runs its change
  with `Start-UniGetUIDev.ps1`, and checks it with screenshots. A second job, which runs
  no agent, opens a draft pull request with the change and the screenshots (published on
  the `agent-assets` branch). The agent job's token can only read and make Copilot
  requests; the write token stays in the pull-request job, which rejects changes to
  `.github/`.

## Minimal job

```yaml
jobs:
  agent:
    runs-on: windows-latest
    timeout-minutes: 60
    permissions:
      contents: read
    steps:
      - uses: actions/checkout@v7

      # Optional: test this commit instead of the latest release.
      - id: build
        uses: ./.github/actions/unigetui-build

      - uses: ./.github/actions/desktop-session
        with:
          desktop-size: 1920x1080

      - uses: ./.github/actions/unigetui-desktop
        with:
          installer-path: ${{ steps.build.outputs.installer-path }}

      # Drive the desktop here: an agent, or your own pwsh steps (see below).
      - shell: pwsh
        run: |
          Import-Module $env:DESKTOP_SESSION_MODULE
          Save-DesktopScreenshot -Name 'unigetui.png'

      - if: always()
        uses: ./.github/actions/desktop-session-cleanup
        with:
          artifact-name: desktop-session
```

Leave out `unigetui-build` and `installer-path` to install the latest release, or pass
`release: v2026.3.2` for a specific one.

## Driving the desktop

Any later step can run `Import-Module $env:DESKTOP_SESSION_MODULE` to load
[`DesktopSession.psm1`](desktop-session/DesktopSession.psm1). It reads the
`DESKTOP_SESSION_*` variables, so it needs no setup:

| Command | Does |
|---|---|
| `Save-DesktopScreenshot -Name shot.png` | Saves a PNG of the desktop in the artifacts directory and returns its path. |
| `Test-DesktopScreenshot -Path $png` | Throws if the screenshot is the wrong size or blank. |
| `Get-DesktopScreenshotChange -BeforePath $a -AfterPath $b [-Rect $window]` | Share of pixels (0 to 1) that changed, overall or inside a window. |
| `Send-DesktopKey 0x01` | Presses and releases scancodes, in order then reversed: `0xE05B, 0x13` is Win+R, `0x1C` is Enter. |
| `Send-DesktopText 'hello'` | Types text as Unicode key events. |
| `Move-DesktopMouse -X 100 -Y 200`, `Invoke-DesktopClick -X 100 -Y 200 [-Button right]` | Mouse input. |
| `Invoke-DesktopCommand { ... }` | Runs a script block *inside* the desktop session, so the GUI apps it starts appear on the desktop. |
| `Start-DesktopProcess -FilePath notepad.exe` | Starts a program on the desktop. Returns its process id and session id. |
| `Get-DesktopWindow -ProcessName UniGetUI [-Main]` | Visible top-level windows of a process on the desktop, with handle, title and rectangle. |
| `Show-DesktopWindow -Handle $h [-Maximize]` | Restores or maximizes a window and brings it to the foreground. |
| `Invoke-DesktopAgent <args>` | Any other `ironrdp-agent` command, for example `Invoke-DesktopAgent status`. Throws on a non-zero exit code. |

To call `ironrdp-agent` directly, use
`& $env:DESKTOP_SESSION_AGENT --endpoint $env:DESKTOP_SESSION_ENDPOINT screenshot out.png`.
Check `$LASTEXITCODE` afterwards, because a failing native command does not throw in PowerShell.

The other variables are `DESKTOP_SESSION_ARTIFACTS` (screenshots, logs and session state, all
uploaded by the cleanup action; `RUNNER_TEMP\desktop-session` by default), `DESKTOP_SESSION_PSHOST_ENDPOINT` (JSON with the
remoting port and the desktop's Windows session id), `DESKTOP_SESSION_SIZE` and
`DESKTOP_SESSION_IRONRDP_ROOT`. `unigetui-desktop` also sets `UNIGETUI_EXE`.

## Notes

- UniGetUI's dialogs are drawn inside its main window, not as separate windows. UI Automation
  exposes the open dialog's title as the element with AutomationId `ModalTitle`. Escape closes it.
- IronRDP is pinned through the `ironrdp-ref` input of `desktop-session`. Changing it rebuilds
  and recaches `ironrdp-agent`, which takes about 9 minutes. A cache hit restores it in seconds.
