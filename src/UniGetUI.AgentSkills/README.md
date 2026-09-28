# Devolutions.AgentSkills

A .NET library to install, list, update and remove **agent skills**: folders
with a `SKILL.md` that coding agents such as Claude Code, Codex, Cursor and
GitHub Copilot (about 75 others too) load as extra instructions.

It is the core of the C# port of the [`skills`](https://github.com/vercel-labs/skills)
CLI, compiled directly into UniGetUI as `UniGetUI.AgentSkills`. Its public API
remains in the `Devolutions.AgentSkills` namespace. The companion
`UniGetUI.AgentSkills.Cli` project references this library directly and exposes
`UniGetUI.AgentSkills.Cli.SkillsCli.RunAsync(string[] args)` for in-process CLI
commands; it returns an exit code without terminating the host process.
Pass only the arguments after `skills` (for example, `["list", "--json"]`).
`update` runs its nested `add` commands in-process rather than relaunching
the host executable.
A skill installed by the library can't be told
apart from one installed with `npx skills add`. Both use the same directories,
the same lock files (`~/.agents/.skill-lock.json` and `skills-lock.json`) and
the same sources: GitHub, GitLab, Git URLs, local paths, `/.well-known/skills`
sites, direct downloads and [skills.sh](https://skills.sh) search.

- Targets `net10.0`. It is trim- and NativeAOT-safe: the analyzers run on every build with warnings treated as errors.
- It has no UI and never prompts. It writes nothing to the console and never exits the process.
- Cloning needs `git` on `PATH`. Sources that skills.sh or the GitHub API can serve are fetched over HTTP instead.

## Quick start

```csharp
using Devolutions.AgentSkills;

var skills = new SkillsManager();

// Search skills.sh
foreach (var hit in await skills.SearchAsync("typescript"))
    Console.WriteLine($"{hit.InstallSource}@{hit.Name} ({hit.Installs} installs)");

// Install one skill globally, for the detected agents
var result = await skills.InstallAsync(new SkillInstallRequest
{
    Source = "vercel-labs/agent-skills",
    Skills = ["deploy-to-vercel"],
});

// Installed skills, with their source and content hash
foreach (var s in await skills.GetInstalledSkillsAsync(SkillScope.Global))
    Console.WriteLine($"{s.Name} [{string.Join(", ", s.Agents)}] from {s.Source ?? "local"}");

// Updates
var check = await skills.CheckForUpdatesAsync(SkillScope.Global);
await skills.UpdateAsync(check.Updates);

// Remove
await skills.RemoveAsync(["deploy-to-vercel"], SkillScope.Global);
```

## API

| Method | CLI equivalent | Notes |
| --- | --- | --- |
| `GetAgents()` | | Every supported agent: id, display name, skill directories, and whether it is detected |
| `GetInstalledSkills(scope)` | `skills list --json` | Merged with the lock file: source, ref, hash, install dates |
| `Search(query, owner, limit)` | `skills find <query>` | skills.sh search, sorted by installs |
| `GetAvailableSkills(source)` | `skills add <source> --list` | Lists what a source offers without installing |
| `Install(request)` | `skills add <source> -y` | Returns one outcome per skill; per-agent failures don't throw |
| `Remove(names, scope, agents)` | `skills remove <names> -y` | |
| `CheckForUpdates(scope, names)` | `skills update` (check part) | Read-only; lists skills that can't be checked, with the reason |
| `Update(updates)` / `Update(scope, names)` | `skills update -y` | Reinstalls each skill for the agents that have it |

Every method has an `Async` variant that runs it on the thread pool. Long
operations take an `IProgress<string>`, which receives progress lines and
warnings such as a skipped invalid SKILL.md, and a `CancellationToken`.
Cancelling aborts HTTP requests and kills a running `git`. A source that
can't be fetched, or has none of the requested skills, throws
`SkillsException`. An unknown agent id throws `ArgumentException`.

### Scopes and agents

- `SkillScope.Global` installs under the user's home: `~/.agents/skills`, plus each agent's global directory.
- `SkillScope.Project` installs under `SkillsManagerOptions.ProjectDirectory`, which defaults to the current directory.
- When `SkillInstallRequest.Agents` is null, the library installs for the detected agents plus the "universal" agents that read `.agents/skills`. Pass agent ids (see `GetAgents()`) or `*` to choose.
- `Symlink` mode (the default) keeps one canonical copy and links it into each agent's directory. On Windows the links are junctions, which need no admin rights or Developer Mode.

### Versions

Skills have no version numbers. `InstalledSkillInfo.Hash` and
`SkillUpdate.CurrentHash`/`LatestHash` carry the content hash recorded in the
lock file instead. Depending on the source, that is a Git tree SHA or a
SHA-256 folder hash. A short prefix works as a display version.

### Options

```csharp
new SkillsManager(new SkillsManagerOptions
{
    ProjectDirectory = @"C:\src\my-app",   // for SkillScope.Project
    GitHubToken = token,                   // default: GITHUB_TOKEN / GH_TOKEN
    EnableTelemetry = false,               // skills.sh install counts; off by default
    HomeDirectory = sandboxHome,           // tests and sandboxes
    NotionCliPath = ntnPath,               // default: ntn on PATH
});
```

### Notion

A Notion skills database is a source: pass its address
(`https://app.notion.com/p/<id>` or a `notion.so` link) to `GetAvailableSkills`
and `Install`, or the address of one skill page. Skills are named the way
Notion names them when it downloads them: `Proofreader / copyeditor` becomes
`proofreader-copyeditor`. The page title works too.

Everything goes through the [Notion CLI](https://developers.notion.com/cli)
(`ntn`), which keeps the sign-in in the OS credential store, so the library
never sees a Notion token:

| Method | Notes |
| --- | --- |
| `GetNotionStatus()` | Whether `ntn` is installed and signed in, with the workspace name |
| `BeginNotionSignIn()` | The Notion page to open in the user's browser, and the code it shows |
| `CompleteNotionSignIn()` | Waits, up to 15 minutes, for the user to approve |
| `SignOutOfNotion()` | Deletes the token `ntn` keeps |

A failure a host can act on throws `SkillsException` with `Failure` set to
`NotionCliMissing`, `NotionSignedOut` or `NotionNotFound` (not shared with the
signed-in workspace).

Global installs from Notion are tracked in the global lock, with the page and
its Notion version id, so `CheckForUpdates` finds skills edited in Notion. The
reference CLI doesn't track them, and its `skills update` skips these entries.

## Not supported

- Notion packs (`notion`), which need the interactive CLI to pick them.
- `skills use`, `init` and `experimental_sync`, which are CLI workflows.

## License

MIT. Derived from [vercel-labs/skills](https://github.com/vercel-labs/skills) (MIT, © Vercel, Inc.).
