using System.Diagnostics;
using UniGetUI.Core.IconEngine;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.PackageEngine.Managers.SnapManager;

internal sealed class SnapPkgDetailsHelper : BasePkgDetailsHelper
{
    public SnapPkgDetailsHelper(Snap manager)
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

        var descLines = new List<string>();
        bool inDescription = false;
        bool inChannels = false;

        while (p.StandardOutput.ReadLine() is { } line)
        {
            logger.AddToStdOut(line);

            if (line.StartsWith("channels:", StringComparison.Ordinal))
            {
                inChannels = true;
                inDescription = false;
                continue;
            }

            if (inChannels && line.StartsWith("  "))
                continue;

            if (inChannels && !line.StartsWith("  "))
                inChannels = false;

            if (inDescription)
            {
                if (line.StartsWith("  "))
                {
                    var descLine = line.TrimStart();
                    if (descLine != ".") descLines.Add(descLine);
                    continue;
                }
                else
                {
                    inDescription = false;
                }
            }

            var colonIdx = line.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx <= 0) continue;

            var key = line[..colonIdx].Trim().ToLowerInvariant();
            var value = line[(colonIdx + 1)..].Trim();

            switch (key)
            {
                case "summary":
                    details.Description = value;
                    break;
                case "publisher":
                    details.Publisher = value.Replace("\u2713", "").Trim();
                    break;
                case "license":
                    details.License = value;
                    break;
                case "homepage":
                    if (Uri.TryCreate(value, UriKind.Absolute, out var homepage))
                        details.HomepageUrl = homepage;
                    break;
                case "store-url":
                    if (Uri.TryCreate(value, UriKind.Absolute, out var storeUrl))
                        details.ManifestUrl = storeUrl;
                    break;
                case "description":
                    if (value == "|")
                    {
                        details.Description = "";
                        inDescription = true;
                    }
                    else
                    {
                        details.Description = value;
                    }
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
        => Array.Empty<Uri>();

    protected override string? GetInstallLocation_UnSafe(IPackage package)
        => $"/snap/{package.Id}/current";

    protected override IReadOnlyList<string> GetInstallableVersions_UnSafe(IPackage package)
        => throw new InvalidOperationException("Snap does not support installing arbitrary versions");
}
