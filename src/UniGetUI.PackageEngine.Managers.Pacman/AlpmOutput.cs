using System.Diagnostics;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.Managers.PacmanManager;

public static class AlpmOutput
{
    private static readonly char[] VersionConstraintSeparators = ['>', '<', '=', ':'];

    public static void AddDependencies(IPackageDetails details, string value, bool mandatory)
    {
        foreach (var dep in value.Split("  ", StringSplitOptions.RemoveEmptyEntries))
        {
            var depName = mandatory
                ? dep.Split(VersionConstraintSeparators)[0].Trim()
                : dep.Split(':')[0].Trim();
            if (depName.Length > 0)
                details.Dependencies.Add(new() { Name = depName, Version = "", Mandatory = mandatory });
        }
    }

    public static string? FindInstallLocation(string executablePath, string packageId)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        p.StartInfo.ArgumentList.Add("-Ql");
        p.StartInfo.ArgumentList.Add(packageId);
        p.Start();
        var stderr = p.StandardError.ReadToEndAsync();

        // Output format: "<name> <path>" — find the first directory entry.
        // Must drain stdout fully before WaitForExit to avoid a pipe-full deadlock
        // on packages with thousands of file entries (e.g. linux-firmware).
        string? result = null;
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            if (result is not null) continue;
            var spaceIdx = line.IndexOf(' ');
            if (spaceIdx < 0) continue;
            var path = line[(spaceIdx + 1)..].Trim();
            if (Directory.Exists(path))
                result = path;
        }

        stderr.Wait();
        p.WaitForExit();
        return result;
    }
}
