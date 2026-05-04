namespace UniGetUI.Interface;

internal enum IpcCliParseStatus
{
    NotIpcCommand,
    Success,
    Help,
    Error,
}

internal sealed record IpcCliParseResult(
    IpcCliParseStatus Status,
    string? Command = null,
    string[]? EffectiveArgs = null,
    string? Message = null
);

public static class IpcCliSyntax
{
    private static readonly HashSet<string> GlobalOptionsWithValue = new(StringComparer.OrdinalIgnoreCase)
    {
        IpcTransportOptions.CliTransportArgument,
        IpcTransportOptions.CliTcpPortArgument,
        IpcTransportOptions.CliNamedPipeArgument,
    };

    internal static IpcCliParseResult Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return new(IpcCliParseStatus.NotIpcCommand);
        }

        if (args.Any(arg => string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase))
            || args.Any(arg => string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)))
        {
            return new(IpcCliParseStatus.Help);
        }

        List<int> commandIndexes = [];
        HashSet<int> consumedIndexes = [];
        List<string> leadingGlobalArgs = [];
        bool commandStarted = false;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (!commandStarted)
            {
                if (GlobalOptionsWithValue.Contains(arg))
                {
                    leadingGlobalArgs.Add(arg);
                    consumedIndexes.Add(i);
                    if (i + 1 < args.Count)
                    {
                        leadingGlobalArgs.Add(args[i + 1]);
                        consumedIndexes.Add(i + 1);
                        i++;
                    }

                    continue;
                }

                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    return new(IpcCliParseStatus.NotIpcCommand);
                }

                commandStarted = true;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                break;
            }

            commandIndexes.Add(i);
            consumedIndexes.Add(i);
        }

        if (commandIndexes.Count == 0)
        {
            return new(IpcCliParseStatus.NotIpcCommand);
        }

        string[] path = commandIndexes
            .Select(index => NormalizeToken(args[index]))
            .ToArray();

        if (path is ["help"])
        {
            return new(IpcCliParseStatus.Help);
        }

        string? command = TryMapCommand(path, out List<string> injectedArgs);
        if (command is null)
        {
            return new(
                IpcCliParseStatus.NotIpcCommand,
                Message: $"Unknown command path \"{string.Join(" ", path)}\"."
            );
        }

        List<string> remainingArgs = [];
        for (int i = 0; i < args.Count; i++)
        {
            if (!consumedIndexes.Contains(i))
            {
                remainingArgs.Add(args[i]);
            }
        }

        RewriteArgumentAliases(command, remainingArgs);

        return new(
            IpcCliParseStatus.Success,
            Command: command,
            EffectiveArgs: [.. leadingGlobalArgs, .. injectedArgs, .. remainingArgs]
        );
    }

    public static bool IsIpcCommand(IReadOnlyList<string> args)
    {
        return Parse(args).Status is IpcCliParseStatus.Success or IpcCliParseStatus.Help;
    }

    public static bool HasVerbCommand(IReadOnlyList<string> args)
    {
        int firstArgumentIndex = GetFirstNonGlobalArgumentIndex(args);
        if (firstArgumentIndex < 0)
        {
            return false;
        }

        string firstArgument = args[firstArgumentIndex];
        if (firstArgument.StartsWith("-", StringComparison.Ordinal))
        {
            return false;
        }

        return Parse(args).Status is IpcCliParseStatus.Success or IpcCliParseStatus.Help;
    }

    public static string GetHelpText()
    {
        return """
Usage:
  unigetui [global-options] <command> [subcommand] [options]

Global options:
  --transport {tcp|named-pipe}
  --tcp-port <port>
  --pipe-name <name>

Core commands:
  status
  version
  app status|show|navigate|quit
  operation list|get|output|wait|cancel|retry|reorder|forget
  manager list|maintenance|reload|set-executable|clear-executable|action|enable|disable
  manager notifications enable|disable
  source list|add|remove
  settings list|get|set|clear|reset
  settings secure list|get|set
  shortcut list|set|reset|reset-all
  log app|operations|manager
  backup status
  backup local create
  backup cloud list|create|download|restore
  backup github login start|complete
  backup github logout
  bundle get|reset|import|export|add|remove|install
  package search|details|versions|installed|updates|install|download|reinstall|repair|update|uninstall|show
  package ignored list|add|remove
  package update-all
  package update-manager

Examples:
  unigetui status
  unigetui app status
  unigetui package search --manager dotnet-tool --query dotnetsay
  unigetui package install --manager dotnet-tool --id dotnetsay --version 2.1.4 --scope Global
  unigetui operation wait --id 123 --timeout 300
  unigetui backup local create
  unigetui backup github login start --launch-browser
""";
    }

    private static string NormalizeToken(string token)
    {
        return token.Trim().ToLowerInvariant() switch
        {
            "operations" => "operation",
            "packages" => "package",
            "managers" => "manager",
            "sources" => "source",
            "shortcuts" => "shortcut",
            "logs" => "log",
            "backups" => "backup",
            "bundles" => "bundle",
            _ => token.Trim().ToLowerInvariant(),
        };
    }

    private static int GetFirstNonGlobalArgumentIndex(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (GlobalOptionsWithValue.Contains(args[i]))
            {
                if (i + 1 < args.Count)
                {
                    i++;
                }

                continue;
            }

            return i;
        }

        return -1;
    }

    private static string? TryMapCommand(string[] path, out List<string> injectedArgs)
    {
        injectedArgs = [];

        return path switch
        {
            ["status"] => "status",
            ["version"] => "get-version",

            ["app", "status"] => "get-app-state",
            ["app", "show"] => "show-app",
            ["app", "navigate"] => "navigate-app",
            ["app", "quit"] => "quit-app",

            ["operation", "list"] => "list-operations",
            ["operation", "get"] => "get-operation",
            ["operation", "output"] => "get-operation-output",
            ["operation", "wait"] => "wait-operation",
            ["operation", "cancel"] => "cancel-operation",
            ["operation", "retry"] => "retry-operation",
            ["operation", "reorder"] => "reorder-operation",
            ["operation", "forget"] => "forget-operation",

            ["manager", "list"] => "list-managers",
            ["manager", "maintenance"] => "get-manager-maintenance",
            ["manager", "reload"] => "reload-manager",
            ["manager", "set-executable"] => "set-manager-executable",
            ["manager", "clear-executable"] => "clear-manager-executable",
            ["manager", "action"] => "run-manager-action",
            ["manager", "enable"] => Inject("set-manager-enabled", injectedArgs, "--enabled", "true"),
            ["manager", "disable"] => Inject("set-manager-enabled", injectedArgs, "--enabled", "false"),
            ["manager", "notifications", "enable"] => Inject(
                "set-manager-update-notifications",
                injectedArgs,
                "--enabled",
                "true"
            ),
            ["manager", "notifications", "disable"] => Inject(
                "set-manager-update-notifications",
                injectedArgs,
                "--enabled",
                "false"
            ),

            ["source", "list"] => "list-sources",
            ["source", "add"] => "add-source",
            ["source", "remove"] => "remove-source",

            ["settings", "list"] => "list-settings",
            ["settings", "get"] => "get-setting",
            ["settings", "set"] => "set-setting",
            ["settings", "clear"] => "clear-setting",
            ["settings", "reset"] => "reset-settings",
            ["settings", "secure", "list"] => "list-secure-settings",
            ["settings", "secure", "get"] => "get-secure-setting",
            ["settings", "secure", "set"] => "set-secure-setting",

            ["shortcut", "list"] => "list-desktop-shortcuts",
            ["shortcut", "set"] => "set-desktop-shortcut",
            ["shortcut", "reset"] => "reset-desktop-shortcut",
            ["shortcut", "reset-all"] => "reset-desktop-shortcuts",

            ["log", "app"] => "get-app-log",
            ["log", "operation"] => "get-operation-history",
            ["log", "operations"] => "get-operation-history",
            ["log", "manager"] => "get-manager-log",

            ["backup", "status"] => "get-backup-status",
            ["backup", "local", "create"] => "create-local-backup",
            ["backup", "cloud", "list"] => "list-cloud-backups",
            ["backup", "cloud", "create"] => "create-cloud-backup",
            ["backup", "cloud", "download"] => "download-cloud-backup",
            ["backup", "cloud", "restore"] => "restore-cloud-backup",
            ["backup", "github", "login", "start"] => "start-github-sign-in",
            ["backup", "github", "login", "complete"] => "complete-github-sign-in",
            ["backup", "github", "logout"] => "sign-out-github",

            ["bundle", "get"] => "get-bundle",
            ["bundle", "reset"] => "reset-bundle",
            ["bundle", "import"] => "import-bundle",
            ["bundle", "export"] => "export-bundle",
            ["bundle", "add"] => "add-bundle-package",
            ["bundle", "remove"] => "remove-bundle-package",
            ["bundle", "install"] => "install-bundle",

            ["package", "search"] => "search-packages",
            ["package", "details"] => "package-details",
            ["package", "versions"] => "package-versions",
            ["package", "installed"] => "list-installed",
            ["package", "updates"] => "get-updates",
            ["package", "install"] => "install-package",
            ["package", "download"] => "download-package",
            ["package", "reinstall"] => "reinstall-package",
            ["package", "repair"] => "uninstall-then-reinstall-package",
            ["package", "update"] => "update-package",
            ["package", "uninstall"] => "uninstall-package",
            ["package", "show"] => "show-package",
            ["package", "ignored", "list"] => "list-ignored-updates",
            ["package", "ignored", "add"] => "ignore-package",
            ["package", "ignored", "remove"] => "unignore-package",
            ["package", "update-all"] => "update-all",
            ["package", "update-manager"] => "update-manager",

            _ => null,
        };
    }

    private static string Inject(
        string command,
        List<string> injectedArgs,
        params string[] args
    )
    {
        injectedArgs.AddRange(args);
        return command;
    }

    private static void RewriteArgumentAliases(string command, List<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            args[i] = command switch
            {
                "get-operation" or "get-operation-output" or "wait-operation" or "cancel-operation"
                    or "retry-operation" or "reorder-operation" or "forget-operation"
                    when string.Equals(args[i], "--id", StringComparison.OrdinalIgnoreCase)
                        => "--operation-id",

                "package-details" or "package-versions" or "install-package" or "download-package"
                    or "reinstall-package" or "update-package" or "uninstall-package"
                    or "uninstall-then-reinstall-package" or "ignore-package"
                    or "unignore-package" or "show-package" or "add-bundle-package"
                    or "remove-bundle-package"
                    when string.Equals(args[i], "--id", StringComparison.OrdinalIgnoreCase)
                        => "--package-id",

                "package-details" or "package-versions" or "install-package" or "download-package"
                    or "reinstall-package" or "update-package" or "uninstall-package"
                    or "uninstall-then-reinstall-package" or "ignore-package"
                    or "unignore-package" or "show-package" or "add-bundle-package"
                    or "remove-bundle-package"
                    when string.Equals(args[i], "--source", StringComparison.OrdinalIgnoreCase)
                        => "--package-source",

                "add-source" or "remove-source"
                    when string.Equals(args[i], "--source-name", StringComparison.OrdinalIgnoreCase)
                        => "--name",

                "add-source" or "remove-source"
                    when string.Equals(args[i], "--source-url", StringComparison.OrdinalIgnoreCase)
                        => "--url",

                "download-cloud-backup" or "restore-cloud-backup"
                    when string.Equals(args[i], "--name", StringComparison.OrdinalIgnoreCase)
                        => "--key",

                _ => args[i],
            };
        }
    }
}
