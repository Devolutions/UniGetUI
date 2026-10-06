# UniGetUI Terminal UI

The terminal UI lets you search, install, update and remove software without opening the
UniGetUI desktop window. It is included with UniGetUI; no separate terminal application or
package is needed.

It uses the same package managers, settings, installation options, ignored updates, bundles
and operation history as the desktop app. **In normal mode, your actions affect the software
on your computer.** Your saved automatic-update and scheduled-maintenance preferences also
apply. Use [demo mode](#try-it-without-changing-your-computer) to explore safely.

## Getting started

Install UniGetUI using the [installation guide](../README.md#installation). You do not need
to build it from source to use the terminal UI.

If `uniget` is on your `PATH`, open a terminal and run:

```shell
uniget tui
```

Otherwise, use the launcher included with your installation:

| Platform | How to start |
| --- | --- |
| Windows | Open PowerShell in the UniGetUI installation folder and run `.\uniget.exe tui`. |
| Linux | Installed `.deb` and `.rpm` packages provide `uniget tui`. From an extracted archive, open its folder and run `./uniget tui`. |
| macOS | After installing in Applications, run `/Applications/UniGetUI.app/Contents/MacOS/uniget tui`. From an extracted archive, run `./uniget tui` in its folder. |

On Windows, selecting **Add to PATH** during installation lets you use `uniget` from any
folder. Open a new terminal after installation. Portable installations do not change `PATH`;
run the launcher from the portable folder. See [the CLI guide](CLI.md#quick-start) for more
launch options and [portable mode](PORTABLE.md) for portable installation details.

**In PowerShell, use `uniget.exe`, not `UniGetUI.exe` directly.** The launcher keeps PowerShell
waiting while you use the TUI. If you need to launch `UniGetUI.exe` itself, run this from its
installation folder:

```powershell
Start-Process -FilePath .\UniGetUI.exe -ArgumentList 'tui' -NoNewWindow -Wait
```

At startup, a version banner and `Initializing UniGetUI engine (loading package managers)...`
appear while your package managers load. The interactive screen opens afterwards; there is
no progress bar during this initial step.

Press **F1** for help at any time and **Ctrl+Q** to quit.

## Try it without changing your computer

Demo mode provides sample packages so you can try searching, installing, updating and
uninstalling without calling real package managers or changing your normal UniGetUI settings:

```shell
uniget tui --fake-data
```

If `uniget` is not on your `PATH`, use the same platform-specific launcher as above, followed
by `tui --fake-data`.

The screen displays **FAKE DATA** throughout the session. Packages and operations are
simulated, downloads do not use the network, and cloud backups stay in a local demo folder.
Links and "open" actions show their target instead of launching another application.
Actions that would change your real machine, such as repairing WinGet or managing desktop
shortcuts, are unavailable.

To keep your demo settings and sample package changes between sessions, choose a dedicated
folder and reuse it:

```shell
uniget tui --fake-data-dir "my-tui-demo"
```

Without a chosen folder, each launch uses a new temporary sandbox.

## Find your way around

The numbered tabs take you to each page. Use **Alt+1** through **Alt+9**, or click a tab.
**Ctrl+Tab** and **Ctrl+Shift+Tab** move to the next and previous pages. Some terminals also
support **Ctrl+1** through **Ctrl+9**.

| Tab | What you can do |
| --- | --- |
| 1 Discover | Search for packages and install them. |
| 2 Updates | Review available updates, update packages, or ignore and pause updates. |
| 3 Installed | View installed packages, uninstall or reinstall them, and open installation locations. |
| 4 Bundles | Create, open, save and install lists of packages. |
| 5 Operations | Follow installation progress and output, cancel operations, or retry failures. |
| 6 Managers | Check package manager status, enable or disable managers, and manage package sources. |
| 7 Settings | Change preferences, default installation options, update behavior, backups and scheduled maintenance. |
| 8 Logs | Read and export UniGetUI and package manager logs. |
| 9 History | Review previous operations, view their logs, or run them again. |

Use **Tab** and **Shift+Tab** to move between controls. **F10** opens the menu bar; use the
arrow keys to choose a menu or action, then **Enter** to activate it. The **Page** menu
contains actions for the page you are viewing.

## Common tasks

### Search for and install a package

1. Open **Discover** with **Alt+1**, press **Ctrl+F**, type a package name or ID, and press **Enter**.
2. When results appear, press **Down** to move to the list and use the arrow keys to highlight
   a package. Press **Enter** to view its details.
3. Back in the list, press **Ctrl+Enter** to install the highlighted package, or **o** to review
   installation options first. Use **Space** to select multiple packages for a batch installation.

The source, sort and search-mode controls above the list let you narrow the results.
Press **m** in the list to see all available actions for the package.

### Update or remove packages

Open **Updates** with **Alt+2** or **Installed** with **Alt+3**. Review the packages and use
**Space** to select or unselect them. **Ctrl+Enter** updates packages on the Updates page
and uninstalls packages on the Installed page. It acts on checked packages, or on the
highlighted package if none are checked.

Updates are selected by default unless you have changed that preference. Check the selection
before starting a batch. Press **m** for other actions, such as ignoring an update, skipping
a version, pausing updates or reinstalling a package.

### Choose installation options

In a package list, press **o** to choose options such as version, architecture, installation
scope or location, additional command-line arguments, and administrator or interactive mode.
Available choices depend on the package manager. Some operations require administrator
permission.

### Follow an operation or investigate a failure

Open **Operations** with **Alt+5** to see progress and command output. Use its actions to
cancel or retry an operation. **History** keeps previous operations and their logs, while
**Logs** provides application and package manager diagnostics.

### Work with package bundles

Use **Bundles** to create or open a list of packages, save it, and install its contents.
Supported bundle formats are `.ubundle`, `.json`, `.yaml` and `.xml`. You can also open an
existing bundle when starting the TUI:

```shell
uniget tui "my-packages.ubundle"
```

Review any bundle security warnings before confirming installation.

## Keyboard

These shortcuts apply when the relevant page or package list has focus. When typing into
a text field, ordinary letters enter text instead of running package actions.

| Keys | Action |
| --- | --- |
| Alt+1 through Alt+9 | Go to a numbered page tab |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / previous page |
| Tab / Shift+Tab | Next / previous control |
| Esc | Close a dialog or menu, or return from a search field to the list |
| F10 | Open the menu bar |
| Alt+letter | Open a menu using its highlighted access letter |
| F1 | Open help |
| F5 or Ctrl+R | Reload the current page |
| Ctrl+F or `/` | Focus search or filtering |
| Ctrl+A | Select all packages |
| Ctrl+Q | Quit |
| Space | Select or unselect the highlighted package |
| Enter | View package details |
| Ctrl+Enter | Install, update or uninstall, depending on the page |
| `o` or Alt+Enter | Open installation options |
| `m` | Show all actions for the package |
| `i` / `u` / `x` | Install / update / uninstall |
| `b` | Add packages to a bundle |
| `g` | Ignore updates |
| `f` / `s` / F3 | Filter by source / sort / change search mode |

Inside dialogs, use **Tab** to move between fields, the arrow keys to change choices,
**Enter** to activate a control, and **Esc** to cancel.

## Themes

Choose a theme in **View > Theme** or **Settings > User interface preferences > Theme**.
The change appears immediately and is saved for future sessions.

The default is **Devolutions - Graphite**. Other choices include Devolutions Black, Light,
Dark Blue, Blue, Gray and High contrast, plus Dracula, Monokai, Nord, Gruvbox Dark,
Solarized Dark, Solarized Light, One Dark, Tokyo Night and Catppuccin Mocha.

Use **High contrast** if you need stronger separation between text, controls and backgrounds.
Older 16-colour consoles display approximate theme colours.

For a temporary theme, use `--theme <id>`. Run `uniget tui --help` to see the theme IDs.

## Startup options

Add these options after `uniget tui`:

| Option | What it does |
| --- | --- |
| `--page <id>` | Start on `discover`, `updates`, `installed`, `bundles`, `operations`, `managers`, `settings`, `logs` or `history`. `help` and `about` open their dialogs. |
| `--theme <id>` | Choose a theme for this session without saving it. |
| `--fake-data` | Explore with simulated packages and separate settings. |
| `--fake-data-dir <folder>` | Explore with simulated packages and keep demo state in the chosen folder. |
| `--updateapps` | Start updating every available package after the update check finishes. |
| `<bundle-file>` | Open an existing `.ubundle`, `.json`, `.yaml` or `.xml` package bundle. |
| `--help` | Show usage, settings commands and theme IDs, then exit. |

For example, open the Updates page to review available updates:

```shell
uniget tui --page updates
```

**Use `--updateapps` only when you intend to update all available packages.** Unlike
`--page updates`, it requests automatic updates rather than just choosing the starting page.
Your saved automatic-update preferences still apply without this option.

Settings import, export and individual setting changes can also be run without opening the
interactive screen. Use `uniget tui --help` for their syntax.

## Differences from the desktop app

Most package-management tasks are available in both interfaces. The terminal UI displays
progress and notifications within the terminal rather than using a tray icon or system
notifications. Settings that only affect the desktop window are labelled **(desktop app)**.

The TUI does not update UniGetUI itself. Install application updates through the desktop app
or your usual installation method; the **Help** menu links to the release page.
The Devolutions Agent policy inspector and editor are available only in the desktop app.

## Troubleshooting

| Problem | What to try |
| --- | --- |
| `uniget` is not recognized | Open a new terminal after adding UniGetUI to `PATH`, or run the launcher from the installation folder. |
| PowerShell shows another prompt while the TUI starts, or reports "The parameter is incorrect" | Use `.\uniget.exe tui`, or the explicit `Start-Process` command above. Do not launch `.\UniGetUI.exe tui` directly from an interactive PowerShell prompt. |
| The startup screen stays on "loading package managers" | Initialization must finish before the interactive screen appears. If startup fails, keep the error message for troubleshooting. |
| A package manager is unavailable or a package action is missing | Check **Managers** for its status and enabled state. Available actions depend on the manager and platform. |
| Alt shortcuts do not work | Use **F10** and the arrow keys for menus. On macOS Terminal, enable **Settings > Profiles > Keyboard > Use Option as Meta key**. |
| Alt+Enter switches Windows Terminal to full screen | Use **o** to open installation options instead. |
| Colours look different or menu letters are not underlined | Try a modern terminal such as Windows Terminal. Older consoles have a smaller colour palette. |

Run the TUI in an interactive terminal, not with its input or output redirected to a file or
pipeline.

For more help, press **F1** or use the **Help** menu. When reporting a problem, include your
UniGetUI version, operating system, terminal application, and the error or relevant logs.
**Help > About** provides terminal diagnostics, and **Logs** lets you export logs for review.
