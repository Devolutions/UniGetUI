using System.Diagnostics;
using UniGetUI.Core.IconEngine;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.PackageEngine.Managers.DnfManager;

internal sealed class DnfPkgDetailsHelper : BasePkgDetailsHelper
{
    public DnfPkgDetailsHelper(Dnf manager)
        : base(manager) { }

    protected override void GetDetails_UnSafe(IPackageDetails details)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Manager.Status.ExecutablePath,
                Arguments = $"info {details.Package.Id}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        p.StartInfo.Environment["LANG"] = "C";
        p.StartInfo.Environment["LC_ALL"] = "C";
        IProcessTaskLogger logger = Manager.TaskLogger.CreateNew(
            LoggableTaskType.LoadPackageDetails, p);
        p.Start();

        // dnf info outputs "Key         : value" pairs.
        // Multi-line Description values are indented.
        var descLines = new List<string>();
        bool inDescription = false;

        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);

            // Blank line marks the end of a package block. dnf info can output multiple
            // blocks (e.g. "Installed Packages" then "Available Packages") — stop after
            // the first so the second block doesn't silently overwrite parsed fields.
            if (line.Length == 0) break;

            // Continuation lines for Description are indented with " : " prefix:
            //   "             : second line of the description"
            if (inDescription && line.StartsWith(' '))
            {
                var contColon = line.IndexOf(" : ", StringComparison.Ordinal);
                descLines.Add(contColon >= 0 ? line[(contColon + 3)..].Trim() : line.Trim());
                continue;
            }

            inDescription = false;

            var colonIdx = line.IndexOf(" : ", StringComparison.Ordinal);
            if (colonIdx <= 0) continue;

            var key = line[..colonIdx].Trim();
            var value = line[(colonIdx + 3)..].Trim();

            switch (key)
            {
                case "URL":
                    if (Uri.TryCreate(value, UriKind.Absolute, out var url))
                        details.HomepageUrl = url;
                    break;
                case "Summary":
                    details.Description = value;
                    break;
                case "Description":
                    descLines.Add(value);
                    inDescription = true;
                    break;
                case "License":
                    details.License = value;
                    break;
                case "Packager":
                    details.Publisher = value;
                    break;
                case "Size":
                    // e.g. "1.5 M" or "234 k"
                    details.InstallerSize = ParseDnfSize(value);
                    break;
            }
        }

        if (descLines.Count > 0)
            details.Description = string.Join("\n", descLines);

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
                FileName = "rpm",
                Arguments = $"-ql {package.Id}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        p.Start();

        // Must drain all stdout before WaitForExit — packages like glibc have thousands
        // of file entries and will fill the pipe buffer, causing a deadlock.
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
        => throw new InvalidOperationException("DNF does not support installing arbitrary versions");

    private static long ParseDnfSize(string value)
    {
        // Format: "1.5 M", "234 k", "56 G"
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !double.TryParse(parts[0],
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var num))
            return 0;

        return parts[1].ToUpperInvariant() switch
        {
            "K" => (long)(num * 1024),
            "M" => (long)(num * 1024 * 1024),
            "G" => (long)(num * 1024 * 1024 * 1024),
            _ => (long)num,
        };
    }
}
