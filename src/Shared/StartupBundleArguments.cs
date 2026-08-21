using UniGetUI.Interface;

namespace UniGetUI.Shared;

internal static class StartupBundleArguments
{
    private static readonly string[] BundleExtensions = [".ubundle", ".json", ".yaml", ".xml"];

    private static readonly string[] ValueTakingArguments =
    [
        IpcTransportOptions.TransportArgument,
        IpcTransportOptions.TcpPortArgument,
        IpcTransportOptions.NamedPipeArgument,
        IpcTransportOptions.CliTransportArgument,
        IpcTransportOptions.CliTcpPortArgument,
        IpcTransportOptions.CliNamedPipeArgument,
    ];

    public static List<string> Resolve(IReadOnlyList<string> args, string workingDirectory)
    {
        List<string> bundles = [];
        foreach ((_, string path) in Enumerate(args, workingDirectory))
        {
            bundles.Add(path);
        }

        return bundles;
    }

    public static string[] Normalize(IReadOnlyList<string> args, string workingDirectory)
    {
        string[] normalized = [.. args];
        foreach ((int index, string path) in Enumerate(args, workingDirectory))
        {
            normalized[index] = path;
        }

        return normalized;
    }

    private static IEnumerable<(int Index, string Path)> Enumerate(
        IReadOnlyList<string> args,
        string workingDirectory
    )
    {
        for (int i = 0; i < args.Count; i++)
        {
            string arg = Unquote(args[i]);

            if (arg.StartsWith('-'))
            {
                if (ValueTakingArguments.Contains(arg, StringComparer.Ordinal))
                {
                    i++;
                }

                continue;
            }

            if (TryResolveBundleFile(arg, workingDirectory, out string path))
            {
                yield return (i, path);
            }
        }
    }

    private static bool TryResolveBundleFile(string arg, string workingDirectory, out string fullPath)
    {
        fullPath = string.Empty;

        if (arg.Length == 0)
        {
            return false;
        }

        try
        {
            if (!BundleExtensions.Contains(Path.GetExtension(arg), StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            string candidate = Path.GetFullPath(arg, workingDirectory);
            if (!File.Exists(candidate))
            {
                return false;
            }

            fullPath = candidate;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Unquote(string arg)
    {
        if (arg.Length > 1 && (arg[0] == '"' || arg[0] == '\'') && arg[^1] == arg[0])
        {
            return arg[1..^1];
        }

        return arg;
    }
}
