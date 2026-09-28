namespace UniGetUI.Tui.FakeData;

/// <summary>
/// The command-line "package manager" that fake-data operations actually execute. The fake managers
/// point their executable at the TUI binary itself with a <c>--fake-pm</c> prefix, so a real
/// <c>InstallPackageOperation</c> (options, pre/post commands, process spawn, stdout/stderr streaming,
/// return-code verdicts, loader updates) runs end to end — but the child process only edits the
/// sandboxed <see cref="FakeStateStore"/> file. It never downloads, installs or runs anything else.
///
/// Grammar (argument vector):
/// <code>
///   --fake-pm --state &lt;file&gt; &lt;Manager&gt; install|update|uninstall --id &lt;id&gt; [--version v] [--source s]
///             [--scope s] [--arch a] [--location path] [--interactive] [--skip-hash] [--pre-release]
///             [--remove-data] [extra custom arguments…]
///   --fake-pm --state &lt;file&gt; &lt;Manager&gt; source-add --name &lt;n&gt; --url &lt;u&gt;
///   --fake-pm --state &lt;file&gt; &lt;Manager&gt; source-remove --name &lt;n&gt;
///   --fake-pm --state &lt;file&gt; Scoop scoop-maintenance --task install|uninstall|cleanup   (prints only)
///   --fake-elevate &lt;exe&gt; &lt;fake-pm arguments…&gt;     (the fake elevator; elevates nothing)
/// </code>
/// </summary>
internal static class FakePackageManagerProcess
{
    public const string PmFlag = "--fake-pm";
    public const string ElevateFlag = "--fake-elevate";

    /// <summary>Env var set on fake-data children so a gsudo-style "cache on" probe is answered too.</summary>
    public const string ChildMarkerVariable = "UNIGETUI_TUI_FAKE_CHILD";

    /// <summary>Per-step delay in milliseconds (default 120). Tests set it low to run fast.</summary>
    public const string StepDelayVariable = "UNIGETUI_TUI_FAKE_STEP_MS";

    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0) return false;

        if (args[0] == ElevateFlag)
        {
            Console.WriteLine("[fake elevator] Simulating administrator rights - nothing is actually elevated.");
            // args[1] is the "manager executable" the elevator was asked to run (this binary).
            exitCode = args.Length > 2 && args[2] == PmFlag ? RunPm(args[3..]) : 0;
            return true;
        }

        if (args[0] == PmFlag)
        {
            exitCode = RunPm(args[1..]);
            return true;
        }

        // The operation engine asks a gsudo-compatible elevator to "cache on" admin rights.
        if (args[0] == "cache" && Environment.GetEnvironmentVariable(ChildMarkerVariable) == "1")
        {
            Console.WriteLine("[fake elevator] Pretending to cache administrator rights.");
            return true;
        }

        return false;
    }

    private static int RunPm(string[] args)
    {
        try
        {
            if (args.Length < 4 || args[0] != "--state")
            {
                Console.Error.WriteLine("fake-pm: usage: --fake-pm --state <file> <manager> <verb> [options]");
                return 2;
            }

            string statePath = args[1];
            string manager = args[2];
            string verb = args[3];
            var options = ParseOptions(args[4..], out List<string> extra);
            int stepMs = int.TryParse(Environment.GetEnvironmentVariable(StepDelayVariable), out int ms) ? ms : 120;

            return verb switch
            {
                "install" or "update" or "uninstall" => RunPackageVerb(statePath, manager, verb, options, extra, stepMs),
                "source-add" => RunSourceAdd(statePath, manager, options, stepMs),
                "source-remove" => RunSourceRemove(statePath, manager, options, stepMs),
                "scoop-maintenance" => RunScoopMaintenance(options.GetValueOrDefault("--task", ""), stepMs),
                _ => Fail($"fake-pm: unknown verb \"{verb}\""),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"fake-pm: unexpected error: {ex.Message}");
            return 3;
        }
    }

    private static int RunPackageVerb(string statePath, string manager, string verb,
        Dictionary<string, string> options, List<string> extra, int stepMs)
    {
        string id = options.GetValueOrDefault("--id", "");
        FakeCatalogPackage? package = FakeCatalog.Find(manager, id);
        Console.WriteLine($"[{manager}] fake {verb} requested for \"{id}\" (sandboxed - no real software is touched)");
        foreach (string e in extra)
            Console.WriteLine($"[{manager}] custom argument received: {e}");

        if (package is null)
        {
            Console.Error.WriteLine($"No package found matching input criteria: {id}");
            return 1;
        }

        bool slow = id.Contains("Slow", StringComparison.OrdinalIgnoreCase);
        int delay = slow ? stepMs * 12 : stepMs;
        FakeState current = FakeStateStore.Read(statePath);
        FakeInstalledPackage? installed = current.Installed.Find(p => Same(p.Manager, manager) && Same(p.Id, id));

        if (verb == "uninstall" && installed is null)
        {
            Console.Error.WriteLine($"Package {id} is not installed.");
            return 1;
        }

        string version = options.GetValueOrDefault("--version", "");
        if (version.Length == 0) version = package.LatestVersion;
        if (verb != "uninstall" && !package.Versions.Contains(version))
        {
            Console.Error.WriteLine($"Version {version} of {id} was not found in the fake catalog.");
            return 1;
        }

        if (verb != "uninstall")
        {
            Console.WriteLine($"Found {package.Name} [{package.Id}] Version {version}");
            Console.WriteLine($"Downloading {package.Homepage}/{version}/installer.{package.InstallerType}");
            for (int pct = 0; pct <= 100; pct += 20)
            {
                // Carriage-return progress, like a real CLI's progress bar.
                Console.Write($"\r  {new string('#', pct / 10).PadRight(10, '.')}  {pct}%  ");
                Thread.Sleep(delay);
            }

            Console.WriteLine();
            Console.WriteLine(options.ContainsKey("--skip-hash")
                ? "Skipping installer hash verification (requested)."
                : "Successfully verified installer hash");
            Console.WriteLine(verb == "install" ? "Starting package install..." : "Starting package update...");
        }
        else
        {
            Console.WriteLine($"Starting package uninstall of {package.Name}...");
        }

        Thread.Sleep(delay * 2);

        if (id.Contains("Failing", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Installer failed with exit code: 0x80070643 : Fatal error during {verb}.");
            return 1603;
        }

        FakeStateStore.Update(statePath, state =>
        {
            state.Installed.RemoveAll(p => Same(p.Manager, manager) && Same(p.Id, id));
            if (verb != "uninstall")
            {
                state.Installed.Add(new FakeInstalledPackage
                {
                    Manager = manager,
                    Id = package.Id,
                    Version = version,
                    Source = options.GetValueOrDefault("--source", installed?.Source ?? DefaultSource(manager)),
                    Scope = options.GetValueOrDefault("--scope", installed?.Scope ?? ""),
                    Architecture = options.GetValueOrDefault("--arch", installed?.Architecture ?? ""),
                    Location = options.GetValueOrDefault("--location", installed?.Location ?? ""),
                });
            }

            state.OperationJournal.Add($"{DateTime.UtcNow:O} {manager} {verb} {package.Id} {version}");
        });

        Console.WriteLine(verb switch
        {
            "install" => "Successfully installed",
            "update" => "Successfully updated",
            _ => "Successfully uninstalled",
        });
        return 0;
    }

    /// <summary>The Managers page's Scoop install / uninstall / cleanup: prints what the real commands would do.</summary>
    private static int RunScoopMaintenance(string task, int stepMs)
    {
        string[] steps = task switch
        {
            "install" => ["Installing scoop...", "Installing git...", "Installing scoop-search...", "Done!"],
            "uninstall" => ["Uninstalling scoop...", "Removing all Scoop packages, buckets and preferences...", "Scoop was uninstalled."],
            "cleanup" => ["Cleaning Scoop cache...", "Removing older versions of Scoop apps...", "Done!"],
            _ => [],
        };
        if (steps.Length == 0) return Fail($"fake-pm: unknown scoop-maintenance task \"{task}\"");
        Console.WriteLine($"[Scoop] fake scoop-maintenance \"{task}\" (sandboxed - no real software is touched)");
        foreach (string step in steps)
        {
            Thread.Sleep(stepMs);
            Console.WriteLine(step);
        }

        return 0;
    }

    private static int RunSourceAdd(string statePath, string manager, Dictionary<string, string> options, int stepMs)
    {
        string name = options.GetValueOrDefault("--name", "");
        string url = options.GetValueOrDefault("--url", "");
        if (name.Length == 0) return Fail("fake-pm: source-add needs --name");
        Console.WriteLine($"[{manager}] Adding fake source \"{name}\" ({url})...");
        Thread.Sleep(stepMs * 3);
        bool exists = false;
        FakeStateStore.Update(statePath, state =>
        {
            exists = state.Sources.Exists(s => Same(s.Manager, manager) && Same(s.Name, name));
            if (!exists) state.Sources.Add(new FakeSourceEntry { Manager = manager, Name = name, Url = url });
        });
        if (exists) return Fail($"A source named \"{name}\" already exists.");
        Console.WriteLine("Done.");
        return 0;
    }

    private static int RunSourceRemove(string statePath, string manager, Dictionary<string, string> options, int stepMs)
    {
        string name = options.GetValueOrDefault("--name", "");
        Console.WriteLine($"[{manager}] Removing fake source \"{name}\"...");
        Thread.Sleep(stepMs * 3);
        int removed = 0;
        FakeStateStore.Update(statePath, state =>
            removed = state.Sources.RemoveAll(s => Same(s.Manager, manager) && Same(s.Name, name)));
        if (removed == 0) return Fail($"No source named \"{name}\" was found.");
        Console.WriteLine("Done.");
        return 0;
    }

    private static Dictionary<string, string> ParseOptions(string[] args, out List<string> extra)
    {
        extra = [];
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] valued = ["--id", "--version", "--source", "--scope", "--arch", "--location", "--name", "--url", "--task"];
        for (int i = 0; i < args.Length; i++)
        {
            if (valued.Contains(args[i]) && i + 1 < args.Length)
                result[args[i]] = args[++i];
            else if (args[i] is "--interactive" or "--skip-hash" or "--pre-release" or "--remove-data")
                result[args[i]] = "true";
            else
                extra.Add(args[i]);
        }

        return result;
    }

    private static string DefaultSource(string manager)
        => FakeCatalog.Profile(manager)?.DefaultSourceName ?? "";

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
