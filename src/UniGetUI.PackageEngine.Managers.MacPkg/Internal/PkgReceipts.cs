using System.Diagnostics;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

/// <summary>
/// Reads the receipts that macOS records for every installed .pkg. They are the detection rule:
/// a package is installed when its identifier has a receipt, at the receipt's version.
/// </summary>
internal static class PkgReceipts
{
    internal const string PkgUtilPath = "/usr/sbin/pkgutil";

    /// <summary>Runs pkgutil with the given arguments and returns its exit code and output. Replaceable for tests.</summary>
    internal static Func<IManagerLogger?, string[], (int ExitCode, string Output)> RunPkgUtil { get; set; } = Run;

    public static IReadOnlyDictionary<string, string> GetInstalledVersions(IEnumerable<string> ids, IManagerLogger? logger)
    {
        var (code, output) = RunPkgUtil(logger, ["--pkgs"]);
        if (code != 0)
            throw new InvalidOperationException($"pkgutil --pkgs failed with exit code {code}.");
        var receipts = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string id in ids.Distinct(StringComparer.Ordinal).Where(receipts.Contains))
        {
            var (infoCode, info) = RunPkgUtil(logger, ["--pkg-info", id]);
            if (infoCode == 0 && ParseVersion(info) is { } version)
                versions[id] = version;
        }
        return versions;
    }

    internal static string? ParseVersion(string pkgInfo) => pkgInfo.Split('\n')
        .Select(line => line.Trim())
        .Where(line => line.StartsWith("version:", StringComparison.Ordinal))
        .Select(line => line["version:".Length..].Trim())
        .FirstOrDefault(version => version.Length > 0);

    private static (int, string) Run(IManagerLogger? logger, string[] arguments)
    {
        var startInfo = new ProcessStartInfo(PkgUtilPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        var task = logger?.CreateNew(LoggableTaskType.ListInstalledPackages, process);
        process.Start();
        var stderr = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        task?.AddToStdOut(output);
        task?.AddToStdErr(stderr.Result);
        task?.Close(process.ExitCode);
        return (process.ExitCode, output);
    }
}
