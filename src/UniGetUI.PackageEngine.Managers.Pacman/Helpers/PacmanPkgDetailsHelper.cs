using System.Diagnostics;
using UniGetUI.Core.IconEngine;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.PackageEngine.Managers.PacmanManager;

internal sealed class PacmanPkgDetailsHelper : BasePkgDetailsHelper
{
    public PacmanPkgDetailsHelper(Pacman manager)
        : base(manager) { }

    protected override void GetDetails_UnSafe(IPackageDetails details)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Manager.Status.ExecutablePath,
                Arguments = $"-Si {details.Package.Id}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        IProcessTaskLogger logger = Manager.TaskLogger.CreateNew(
            LoggableTaskType.LoadPackageDetails, p);
        p.Start();

        // "pacman -Si" outputs "Key      : value" pairs (key padded with spaces).
        // Multi-dep fields (Depends On, Optional Deps) wrap onto continuation lines:
        //   "                : next-dep  another-dep"
        // Continuation lines have an empty key (all spaces before " : ").
        string? line;
        string lastKey = "";
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            if (line.Length == 0) break; // blank line separates package records

            var colonIdx = line.IndexOf(" : ", StringComparison.Ordinal);
            if (colonIdx <= 0) continue;

            var key = line[..colonIdx].Trim();
            var value = line[(colonIdx + 3)..].Trim();
            if (value == "None" || value.Length == 0) continue;

            if (key.Length == 0)
            {
                // Continuation line — append to whatever field was active
                switch (lastKey)
                {
                    case "Depends On":
                        foreach (var dep in value.Split("  ", StringSplitOptions.RemoveEmptyEntries))
                        {
                            var depName = dep.Split(new[] { '>', '<', '=', ':' })[0].Trim();
                            if (depName.Length > 0)
                                details.Dependencies.Add(new() { Name = depName, Version = "", Mandatory = true });
                        }
                        break;
                    case "Optional Deps":
                        foreach (var dep in value.Split("  ", StringSplitOptions.RemoveEmptyEntries))
                        {
                            var depName = dep.Split(':')[0].Trim();
                            if (depName.Length > 0)
                                details.Dependencies.Add(new() { Name = depName, Version = "", Mandatory = false });
                        }
                        break;
                }
                continue;
            }

            lastKey = key;
            switch (key)
            {
                case "URL":
                    if (Uri.TryCreate(value, UriKind.Absolute, out var url))
                        details.HomepageUrl = url;
                    break;
                case "Description":
                    details.Description = value;
                    break;
                case "Licenses":
                    details.License = value;
                    break;
                case "Packager":
                    details.Publisher = value;
                    break;
                case "Download Size":
                    details.InstallerSize = ParsePacmanSize(value);
                    break;
                case "Depends On":
                    details.Dependencies.Clear();
                    foreach (var dep in value.Split("  ", StringSplitOptions.RemoveEmptyEntries))
                    {
                        var depName = dep.Split(new[] { '>', '<', '=', ':' })[0].Trim();
                        if (depName.Length > 0)
                            details.Dependencies.Add(new() { Name = depName, Version = "", Mandatory = true });
                    }
                    break;
                case "Optional Deps":
                    foreach (var dep in value.Split("  ", StringSplitOptions.RemoveEmptyEntries))
                    {
                        var depName = dep.Split(':')[0].Trim();
                        if (depName.Length > 0)
                            details.Dependencies.Add(new() { Name = depName, Version = "", Mandatory = false });
                    }
                    break;
            }
        }

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);
    }

    protected override CacheableIcon? GetIcon_UnSafe(IPackage package)
        => throw new NotImplementedException();

    protected override IReadOnlyList<Uri> GetScreenshots_UnSafe(IPackage package)
        => throw new NotImplementedException();

    protected override string? GetInstallLocation_UnSafe(IPackage package)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Manager.Status.ExecutablePath,
                Arguments = $"-Ql {package.Id}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        p.Start();

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

        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return result;
    }

    protected override IReadOnlyList<string> GetInstallableVersions_UnSafe(IPackage package)
        => throw new InvalidOperationException("Pacman does not support installing arbitrary versions");

    private static long ParsePacmanSize(string value)
    {
        // Format: "2.34 MiB", "234.56 KiB", "1.20 GiB"
        var parts = value.Split(' ');
        if (parts.Length < 2 || !double.TryParse(parts[0],
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var num))
            return 0;

        return parts[1] switch
        {
            "KiB" or "kB" => (long)(num * 1024),
            "MiB" or "MB" => (long)(num * 1024 * 1024),
            "GiB" or "GB" => (long)(num * 1024 * 1024 * 1024),
            _ => (long)num,
        };
    }
}
