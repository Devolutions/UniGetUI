using System.Diagnostics;
using UniGetUI.Core.IconEngine;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.PackageEngine.Managers.AptManager;

internal sealed class AptPkgDetailsHelper : BasePkgDetailsHelper
{
    public AptPkgDetailsHelper(Apt manager)
        : base(manager) { }

    protected override void GetDetails_UnSafe(IPackageDetails details)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "apt-cache",
                Arguments = $"show {details.Package.Id}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        IProcessTaskLogger logger = Manager.TaskLogger.CreateNew(
            LoggableTaskType.LoadPackageDetails, p);
        p.Start();

        // apt-cache show outputs key: value pairs, one per line.
        // Multi-line values are indented with a leading space.
        var descLines = new List<string>();
        bool inDescription = false;

        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);

            if (line.Length == 0)
            {
                // Blank line ends the current record — stop after the first record.
                if (inDescription) break;
                continue;
            }

            if (line.StartsWith(' ') && inDescription)
            {
                var descLine = line.TrimStart();
                if (descLine != ".") descLines.Add(descLine);
                continue;
            }

            inDescription = false;

            var colonIdx = line.IndexOf(": ", StringComparison.Ordinal);
            if (colonIdx <= 0) continue;

            var key = line[..colonIdx].Trim();
            var value = line[(colonIdx + 2)..].Trim();

            switch (key)
            {
                case "Version":
                    // Already known; fill in so it's accessible if needed
                    break;
                case "Homepage":
                    if (Uri.TryCreate(value, UriKind.Absolute, out var homepage))
                        details.HomepageUrl = homepage;
                    break;
                case "Description":
                case "Description-en":
                    details.Description = value;
                    inDescription = true;
                    break;
                case "Maintainer":
                    details.Publisher = value;
                    break;
                case "Depends":
                    details.Dependencies.Clear();
                    foreach (var dep in value.Split(','))
                    {
                        var depName = dep.Trim().Split(' ')[0];
                        if (depName.Length > 0)
                            details.Dependencies.Add(new() { Name = depName, Version = "", Mandatory = true });
                    }
                    break;
                case "Recommends":
                    foreach (var dep in value.Split(','))
                    {
                        var depName = dep.Trim().Split(' ')[0];
                        if (depName.Length > 0)
                            details.Dependencies.Add(new() { Name = depName, Version = "", Mandatory = false });
                    }
                    break;
                case "Installed-Size":
                    details.InstallerSize = long.TryParse(value.Replace(" kB", "").Trim(), out var kb)
                        ? kb * 1024
                        : 0;
                    break;
                case "Source":
                    details.ManifestUrl = new Uri($"https://packages.debian.org/source/stable/{value}");
                    break;
            }
        }

        if (descLines.Count > 0)
            details.Description = (details.Description ?? "") + "\n" + string.Join("\n", descLines);

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
        // Debian packages install to system paths; the most reliable way
        // to find the install location is to query dpkg.
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dpkg",
                Arguments = $"-L {package.Id}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        p.Start();

        // Must drain all stdout before WaitForExit — packages with many files
        // will fill the pipe buffer and deadlock if we stop reading early.
        string? result = null;
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            if (result is not null) continue;
            var path = line.Trim();
            if (Directory.Exists(path))
                result = path;
        }

        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return result;
    }

    protected override IReadOnlyList<string> GetInstallableVersions_UnSafe(IPackage package)
        => throw new InvalidOperationException("APT does not support installing arbitrary versions");
}
