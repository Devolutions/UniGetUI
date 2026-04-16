using System.Diagnostics;
using UniGetUI.Core.IconEngine;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
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

        IProcessTaskLogger logger = Manager.TaskLogger.CreateNew(
            Enums.LoggableTaskType.LoadPackageDetails, p);
        p.Start();

        // dnf info outputs "Key         : value" pairs.
        // Multi-line Description values are indented.
        var descLines = new List<string>();
        bool inDescription = false;

        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);

            if (line.Length == 0)
            {
                if (inDescription) break;
                continue;
            }

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
        var firstPath = p.StandardOutput.ReadLine()?.Trim();
        p.WaitForExit();

        if (firstPath is not null && Directory.Exists(firstPath))
            return firstPath;

        return null;
    }

    protected override IReadOnlyList<string> GetInstallableVersions_UnSafe(IPackage package)
        => throw new InvalidOperationException("DNF does not support installing arbitrary versions");

    private static long ParseDnfSize(string value)
    {
        // Format: "1.5 M", "234 k", "56 G"
        var parts = value.Split(' ');
        if (parts.Length < 2 || !double.TryParse(parts[0], out var num))
            return 0;

        return parts[1].ToUpperInvariant() switch
        {
            "K" => (long)(num * 1024),
            "M" => (long)(num * 1024 * 1024),
            "G" => (long)(num * 1024 * 1024 * 1024),
            _   => (long)num,
        };
    }
}
