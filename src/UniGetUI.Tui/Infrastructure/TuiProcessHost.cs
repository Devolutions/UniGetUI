namespace UniGetUI.Tui.Infrastructure;

internal static class TuiProcessHost
{
    public static (string Path, string[] Prefix) Resolve()
        => Resolve(Environment.ProcessPath, AppContext.BaseDirectory, OperatingSystem.IsWindows());

    internal static (string Path, string[] Prefix) Resolve(string? processPath, string baseDirectory, bool windows)
    {
        string exeName = windows ? "UniGetUI.exe" : "UniGetUI";
        if (processPath is not null
            && Path.GetFileName(processPath).Equals(exeName, StringComparison.OrdinalIgnoreCase))
            return (processPath, [TuiAppHost.Command]);

        string candidate = Path.Join(baseDirectory, exeName);
        if (File.Exists(candidate))
            return (candidate, [TuiAppHost.Command]);

        throw new FileNotFoundException("The UniGetUI host is required to launch a terminal subprocess.", candidate);
    }
}
