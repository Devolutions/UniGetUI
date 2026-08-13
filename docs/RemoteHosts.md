# Remote hosts

UniGetUI can inventory and maintain packages on other machines over SSH, and on local WSL distros without SSH. The GUI stays local: you pick **This PC**, a saved SSH host, or an enabled WSL distro on Discover, Updates, and Installed.

## SSH requirements

- OpenSSH client (`ssh` / `ssh.exe`) on the machine running UniGetUI.
- A destination that already works non-interactively: `ssh <host> true` must succeed. UniGetUI does not store passwords, keys, or sudo secrets, and it does not wrap `sshpass`.
- The host key must already be in `known_hosts`. First-connection prompts stay in a terminal, out of band.
- Windows and macOS remotes must have UniGetUI installed. Linux remotes can run agentless against apt, dnf, or pacman (plus snap/flatpak/npm/pip/cargo when present). If UniGetUI is installed on Linux, the agent is preferred so every manager works.

## How SSH connections run

UniGetUI launches:

```
ssh -T -o BatchMode=yes -o StrictHostKeyChecking=yes
    -o ConnectTimeout=10 -o ServerAliveInterval=5 -o ServerAliveCountMax=3
    -- <destination> <remote-command>
```

`BatchMode` refuses password and host-key prompts. Aliases, `IdentityFile`, and `ProxyJump` from `~/.ssh/config` still apply.

Do **not** tunnel the [localhost IPC API](IPC.md). The remote agent is a one-shot `unigetui remote --protocol 1 …` process that writes JSON to stdout. See [CLI.md](CLI.md).

## WSL distributions

On Windows, UniGetUI lists installed WSL distros (`wsl.exe --list --verbose`) and shows them in Settings → Remote hosts. Enable or disable which distros appear in the host picker. Helper VMs such as `docker-desktop` are omitted.

Package commands run through `WslLaunch` (`wslapi.dll`) with Windows pipes for stdin/stdout/stderr. There is no SSH, no stored keys, and no `wsl.exe -d … -- cmd` quoting layer for inventory or updates. Listing still uses `wsl.exe --list --verbose`.

- Selecting a **Stopped** distro starts it. The first WSL 2 boot can take a little while.
- Commands run as the distro's default user. `WslLaunch` has no user argument; elevation is passwordless `sudo -n`, the same as Linux SSH hosts.
- WSL destinations are always treated as Linux. A Windows UniGetUI agent inside the distro is not required.

## Elevation

Interactive UAC and `sudo` password prompts cannot run over BatchMode SSH. WSL uses the default distro user and `sudo -n`.

- System packages without passwordless `sudo -n` (Linux/WSL) or an already-elevated session (Windows SSH) stay **visible and read-only**.
- User-space tools (npm, pip, cargo, and similar) can still update or uninstall when the remote user owns them.

## v1 limits

- Install from Discover is disabled on remotes until remote install is implemented. Search on Discover requires the UniGetUI agent.
- apk and Zypper are not supported yet.
- Bundles, ignored-updates, and IPC package DTOs stay local-only.
- Windows remotes without UniGetUI installed are not supported.
- WSL is Windows-only and does not use SSH.
