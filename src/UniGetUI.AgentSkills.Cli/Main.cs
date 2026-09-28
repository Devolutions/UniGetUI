// `skills` — the CLI for the open agent skills ecosystem.
//
// A self-contained C# port of the TypeScript CLI at
// https://github.com/vercel-labs/skills (entry point: `src/cli.ts`). File
// names mirror the TS modules they port.

using static Skills.Ansi;

namespace Skills;

internal static class Program
{
    public const string Version = "1.7.0";
    private static readonly object InvocationLock = new();

    private static readonly string[] LogoLines =
    [
        "███████╗██╗  ██╗██╗██╗     ██╗     ███████╗",
        "██╔════╝██║ ██╔╝██║██║     ██║     ██╔════╝",
        "███████╗█████╔╝ ██║██║     ██║     ███████╗",
        "╚════██║██╔═██╗ ██║██║     ██║     ╚════██║",
        "███████║██║  ██╗██║███████╗███████╗███████║",
        "╚══════╝╚═╝  ╚═╝╚═╝╚══════╝╚══════╝╚══════╝",
    ];

    private static readonly string[] Grays =
    [
        "\x1b[38;5;250m",
        "\x1b[38;5;248m",
        "\x1b[38;5;245m",
        "\x1b[38;5;243m",
        "\x1b[38;5;240m",
        "\x1b[38;5;238m",
    ];

    private static void ShowLogo()
    {
        Term.OutLine();
        for (var i = 0; i < LogoLines.Length; i++) Term.OutLine($"{Grays[i]}{LogoLines[i]}{Reset}");
    }

    private static void ShowBanner()
    {
        ShowLogo();
        Term.OutLine();
        Term.OutLine($"{Dim}The open agent skills ecosystem{Reset}");
        Term.OutLine();
        Term.OutLine($"  {Dim}${Reset} {Text}skills add {Dim}<package>{Reset}        {Dim}Add a new skill{Reset}");
        Term.OutLine($"  {Dim}${Reset} {Text}skills use {Dim}<package>@<skill>{Reset} {Dim}Use a skill without installing{Reset}");
        Term.OutLine($"  {Dim}${Reset} {Text}skills remove{Reset}               {Dim}Remove installed skills{Reset}");
        Term.OutLine($"  {Dim}${Reset} {Text}skills list{Reset}                 {Dim}List installed skills{Reset}");
        Term.OutLine($"  {Dim}${Reset} {Text}skills find {Dim}[query]{Reset}         {Dim}Search for skills{Reset}");
        Term.OutLine();
        Term.OutLine($"  {Dim}${Reset} {Text}skills update{Reset}               {Dim}Update installed skills{Reset}");
        Term.OutLine();
        Term.OutLine($"  {Dim}${Reset} {Text}skills experimental_install{Reset} {Dim}Restore from skills-lock.json{Reset}");
        Term.OutLine($"  {Dim}${Reset} {Text}skills init {Dim}[name]{Reset}          {Dim}Create a new skill{Reset}");
        Term.OutLine($"  {Dim}${Reset} {Text}skills experimental_sync{Reset}    {Dim}Sync skills from node_modules{Reset}");
        Term.OutLine();
        Term.OutLine($"{Dim}try:{Reset} skills add vercel-labs/agent-skills");
        Term.OutLine();
        Term.OutLine($"Discover more skills at {Text}https://skills.sh/{Reset}");
        Term.OutLine();
    }

    private static void ShowHelp()
    {
        const string b = Bold, d = Dim, r = Reset, t = Text;
        Term.OutLine("\n" + $$"""
            {{b}}Usage:{{r}} skills <command> [options]

            {{b}}Manage Skills:{{r}}
              add <package>        Add a skill package (alias: a)
                                   e.g. vercel-labs/agent-skills
                                        notion
                                        https://notion.so/<skill-page>
                                        https://github.com/vercel-labs/agent-skills
              use <package>@<skill>
                                   Generate a prompt for using one skill without installing it
              preview <package>    Show a skill's files and SKILL.md without installing
                                   (alias: show)
              remove [skills]      Remove installed skills
              list, ls             List installed skills
              find [query]         Search for skills interactively

            {{b}}Find Options:{{r}}
              --owner <owner>        Search only repositories from a GitHub owner

            {{b}}Updates:{{r}}
              update [skills...]   Update skills to latest versions (alias: upgrade)

            {{b}}Update Options:{{r}}
              -g, --global           Update global skills only
              -p, --project          Update project skills only
              -y, --yes              Skip scope prompt (auto-detect: project if in a project, else global)
              --dry-run              Report available updates without changing anything
              --force                Reinstall skills even when they are up to date
              --unpin                Include pinned skills and move them to their default branch

            {{b}}Project:{{r}}
              experimental_install Restore skills from skills-lock.json
              init [name]          Initialize a skill (creates <name>/SKILL.md or ./SKILL.md)
              validate [path]      Check skills against the Agent Skills specification
              experimental_sync    Sync skills from node_modules into agent directories

            {{b}}Add Options:{{r}}
              -g, --global           Install skill globally (user-level) instead of project-level
              -a, --agent <agents>   Specify agents to install to (use '*' for all agents)
              -s, --skill <skills>   Specify skill names to install (use '*' for all skills)
              -l, --list             List available skills in the repository without installing
              -y, --yes              Skip confirmation prompts
              --copy                 Copy files instead of symlinking to agent directories
              --metadata <json>      Attach valid JSON to the install telemetry event
              --subagent <names>     Install to Eve subagents (use 'root' for the root agent)
              --all                  Shorthand for --skill '*' --agent '*' -y
              --full-depth           Search all subdirectories even when a root SKILL.md exists
              --pin <ref>            Install from a tag, branch or commit and pin it ('latest' = newest release)
              --json                 Output results as JSON (machine-readable, no ANSI codes)

            {{b}}Use Options:{{r}}
              -s, --skill <skill>    Specify the skill to use
              -a, --agent <agent>    Start one supported agent interactively
              --full-depth           Search all subdirectories even when a root SKILL.md exists

            {{b}}Preview Options:{{r}}
              -s, --skill <skill>    Specify the skill to preview
              --file <path>          Print one file of the skill instead of the overview
              --full-depth           Search all subdirectories even when a root SKILL.md exists
              --json                 Output as JSON (machine-readable, no ANSI codes)
              --no-pager             Do not page the output

            {{b}}Validate Options:{{r}}
              --fix                  Remove install tracking metadata from SKILL.md files
              --strict               Fail on warnings as well as errors
              --json                 Output as JSON (machine-readable, no ANSI codes)

            {{b}}Remove Options:{{r}}
              -g, --global           Remove from global scope
              -a, --agent <agents>   Remove from specific agents (omit to clean all agent links)
              -s, --skill <skills>   Specify skills to remove (use '*' for all skills)
              -y, --yes              Skip confirmation prompts
              --all                  Remove every installed skill (-y implied). Do not combine with named skills.
            {{"  "}}
            {{b}}Experimental Sync Options:{{r}}
              -a, --agent <agents>   Specify agents to install to (use '*' for all agents)
              -y, --yes              Skip confirmation prompts

            {{b}}List Options:{{r}}
              -g, --global           List global skills (default: project)
              -a, --agent <agents>   Filter by specific agents
              --json                 Output as JSON (machine-readable, no ANSI codes)

            {{b}}Options:{{r}}
              --help, -h        Show this help message
              --version, -v     Show version number

            {{b}}Examples:{{r}}
              {{d}}${{r}} skills add vercel-labs/agent-skills
              {{d}}${{r}} skills use vercel-labs/agent-skills@vercel-optimize | claude
              {{d}}${{r}} skills use vercel-labs/agent-skills --skill vercel-optimize --agent claude-code
              {{d}}${{r}} skills preview vercel-labs/agent-skills@web-design-guidelines
              {{d}}${{r}} skills add vercel-labs/agent-skills -g
              {{d}}${{r}} skills add vercel-labs/agent-skills --agent claude-code cursor
              {{d}}${{r}} skills add vercel-labs/agent-skills --skill pr-review commit
              {{d}}${{r}} skills add vercel-labs/agent-skills --json -y {{d}}# JSON output{{r}}
              {{d}}${{r}} skills remove                        {{d}}# interactive remove{{r}}
              {{d}}${{r}} skills remove web-design             {{d}}# remove by name{{r}}
              {{d}}${{r}} skills rm --global frontend-design
              {{d}}${{r}} skills list                          {{d}}# list project skills{{r}}
              {{d}}${{r}} skills ls -g                         {{d}}# list global skills{{r}}
              {{d}}${{r}} skills ls -a claude-code             {{d}}# filter by agent{{r}}
              {{d}}${{r}} skills ls --json                      {{d}}# JSON output{{r}}
              {{d}}${{r}} skills find                          {{d}}# interactive search{{r}}
              {{d}}${{r}} skills find typescript               {{d}}# search by keyword{{r}}
              {{d}}${{r}} skills find react --owner vercel     {{d}}# search within an owner{{r}}
              {{d}}${{r}} skills update
              {{d}}${{r}} skills update my-skill             {{d}}# update a single skill{{r}}
              {{d}}${{r}} skills update -g                    {{d}}# update global skills only{{r}}
              {{d}}${{r}} skills update --dry-run             {{d}}# check without installing{{r}}
              {{d}}${{r}} skills add vercel-labs/agent-skills --pin v1.0.0
              {{d}}${{r}} skills experimental_install            {{d}}# restore from skills-lock.json{{r}}
              {{d}}${{r}} skills init my-skill
              {{d}}${{r}} skills validate                     {{d}}# check skills before publishing{{r}}
              {{d}}${{r}} skills experimental_sync              {{d}}# sync from node_modules{{r}}
              {{d}}${{r}} skills experimental_sync -y           {{d}}# sync without prompts{{r}}

            Discover more skills at {{t}}https://skills.sh/{{r}}
            """ + "\n");
    }

    private static void ShowRemoveHelp()
    {
        const string b = Bold, d = Dim, r = Reset, t = Text;
        Term.OutLine("\n" + $$"""
            {{b}}Usage:{{r}} skills remove [skills...] [options]

            {{b}}Description:{{r}}
              Remove installed skills from agents. If no skill names are provided,
              an interactive selection menu will be shown.

            {{b}}Arguments:{{r}}
              skills            Optional skill names to remove (space-separated)

            {{b}}Options:{{r}}
              -g, --global       Remove from global scope (~/) instead of project scope
              -a, --agent        Remove from specific agents (omit to clean all agent links)
              -s, --skill        Specify skills to remove (use '*' for all skills)
              -y, --yes          Skip confirmation prompts
              --all              Remove every installed skill (-y implied). Do not combine with named skills.

            {{b}}Examples:{{r}}
              {{d}}${{r}} skills remove                           {{d}}# interactive selection{{r}}
              {{d}}${{r}} skills remove my-skill                   {{d}}# remove specific skill{{r}}
              {{d}}${{r}} skills remove skill1 skill2 -y           {{d}}# remove multiple skills{{r}}
              {{d}}${{r}} skills remove --global my-skill          {{d}}# remove from global scope{{r}}
              {{d}}${{r}} skills rm --agent claude-code my-skill   {{d}}# remove from specific agent{{r}}
              {{d}}${{r}} skills remove --all                      {{d}}# remove all skills{{r}}
              {{d}}${{r}} skills remove --skill '*' -a cursor      {{d}}# remove all skills from cursor{{r}}

            Discover more skills at {{t}}https://skills.sh/{{r}}
            """ + "\n");
    }

    private static void RunInit(IReadOnlyList<string> args)
    {
        var cwd = Sys.Cwd();
        var hasName = args.Count > 0;
        var skillName = hasName && args[0].Length > 0 ? args[0] : NodePath.Basename(cwd);
        var skillDir = hasName ? NodePath.Join(cwd, skillName) : cwd;
        var skillFile = NodePath.Join(skillDir, "SKILL.md");
        var display = hasName ? $"{skillName}/SKILL.md" : "SKILL.md";

        if (Fs.Exists(skillFile))
        {
            Term.OutLine($"{Text}Skill already exists at {Dim}{display}{Reset}");
            return;
        }
        try
        {
            if (hasName) Directory.CreateDirectory(skillDir);
            File.WriteAllText(skillFile,
                $"---\nname: {skillName}\ndescription: A brief description of what this skill does\n---\n\n# {skillName}\n\nInstructions for the agent to follow when this skill is activated.\n\n## When to use\n\nDescribe when this skill should be used.\n\n## Instructions\n\n1. First step\n2. Second step\n3. Additional steps as needed\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Term.ErrLine(e.Message);
            Term.Exit(1);
        }
        Term.OutLine($"{Text}Initialized skill: {Dim}{skillName}{Reset}");
        Term.OutLine();
        Term.OutLine($"{Dim}Created:{Reset}");
        Term.OutLine($"  {display}");
        Term.OutLine();
        Term.OutLine($"{Dim}Next steps:{Reset}");
        Term.OutLine($"  1. Edit {Text}{display}{Reset} to define your skill instructions");
        Term.OutLine($"  2. Update the {Text}name{Reset} and {Text}description{Reset} in the frontmatter");
        Term.OutLine();
        Term.OutLine($"{Dim}Publishing:{Reset}");
        Term.OutLine($"  {Dim}GitHub:{Reset}  Push to a repo, then {Text}skills add <owner>/<repo>{Reset}");
        Term.OutLine($"  {Dim}URL:{Reset}     Host the file, then {Text}skills add https://example.com/{display}{Reset}");
        Term.OutLine();
        Term.OutLine($"Browse existing skills for inspiration at {Text}https://skills.sh/{Reset}");
        Term.OutLine();
    }

    private static void Run(string[] args)
    {
        var inAgent = DetectAgent.IsRunningInAgent();
        if (args.Length == 0)
        {
            if (!inAgent) ShowBanner();
            return;
        }
        var command = args[0];
        var rest = args[1..];

        // Subcommand --help / -h short-circuits before dispatch so side-effecting
        // handlers never run when help was requested.
        if (command is not ("--help" or "-h" or "--version" or "-v") && rest.Any(a => a is "--help" or "-h"))
        {
            if (command is "remove" or "rm" or "r") ShowRemoveHelp();
            else if (command is "preview" or "show") PreviewCommand.PrintHelp();
            else if (command == "validate") ValidateCommand.PrintHelp();
            else ShowHelp();
            return;
        }

        switch (command)
        {
            case "find" or "search" or "f" or "s":
                if (!inAgent) ShowLogo();
                Term.OutLine();
                FindCommand.Run(rest);
                break;
            case "init":
                if (!inAgent) ShowLogo();
                Term.OutLine();
                RunInit(rest);
                break;
            case "experimental_install":
                if (!inAgent) ShowLogo();
                InstallLockCommand.Run(rest);
                break;
            case "i" or "install" or "a" or "add":
            {
                var (source, opts, errors) = AddCommand.ParseOptions(rest);
                if (!inAgent && !opts.Json) ShowLogo();
                if (errors.Count > 0)
                {
                    foreach (var e in errors) Term.ErrLine($"Error: {e}");
                    if (opts.Json) Term.OutLine("[]");
                    Term.ExitCode = 1;
                    return;
                }
                AddCommand.Run(source, opts);
                break;
            }
            case "use":
            {
                var (source, opts, errors) = UseCommand.ParseOptions(rest);
                UseCommand.Run(source, opts, errors);
                break;
            }
            case "preview" or "show":
                PreviewCommand.Run(rest);
                break;
            case "validate":
                ValidateCommand.Run(rest);
                break;
            case "remove" or "rm" or "r":
            {
                var (skills, opts) = RemoveCommand.ParseOptions(rest);
                RemoveCommand.Run(skills, opts);
                break;
            }
            case "experimental_sync":
                if (!inAgent) ShowLogo();
                SyncCommand.Run(SyncCommand.ParseOptions(rest));
                break;
            case "list" or "ls":
                ListCommand.Run(rest);
                break;
            case "check" or "update" or "upgrade":
                UpdateCommand.Run(rest);
                break;
            case "--help" or "-h":
                ShowHelp();
                break;
            case "--version" or "-v":
                Term.OutLine(Version);
                break;
            default:
                Term.OutLine($"Unknown command: {command}");
                Term.OutLine($"Run {Bold}skills --help{Reset} for usage.");
                Term.ExitCode = 1;
                break;
        }
    }

    /// Point the library core at the terminal: its warnings go to stderr and
    /// requests identify as the CLI.
    internal static void ConfigureCore()
    {
        Sys.UserAgent = $"skills-cli/{Version}";
        Sys.WarningSink = Term.ErrLine;
        Telemetry.SetVersion(Version);
    }

    internal static int RunCommand(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        lock (InvocationLock)
        {
            var previousArguments = Pc.CommandArguments;
            Pc.CommandArguments = args;
            Term.ExitCode = 0;
            Term.StdoutRedirected = false;
            Term.ClearExitHooks();
            // Node translates ANSI sequences for the Windows console; enable VT
            // processing so colors and cursor movement render the same way.
            Term.EnableVirtualTerminal();
            ConfigureCore();
            var exitCode = 1;
            try
            {
                Run(args);
                Telemetry.Flush(TimeSpan.FromSeconds(5));
                exitCode = Term.ExitCode;
            }
            catch (CliExitException e)
            {
                exitCode = e.Code;
            }
            finally
            {
                Ui.OnProcessExit(exitCode);
                Ui.RestoreTerminal();
                Term.ClearExitHooks();
                Term.StdoutRedirected = false;
                Pc.CommandArguments = previousArguments;
            }
            return exitCode;
        }
    }

    public static int Main(string[] args) => RunCommand(args);
}
