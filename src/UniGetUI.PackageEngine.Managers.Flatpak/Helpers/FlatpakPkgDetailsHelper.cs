using System.Diagnostics;
using UniGetUI.Core.IconEngine;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.PackageEngine.Managers.FlatpakManager;

internal sealed class FlatpakPkgDetailsHelper : BasePkgDetailsHelper
{
    public FlatpakPkgDetailsHelper(Flatpak manager)
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

        while (p.StandardOutput.ReadLine() is { } line)
        {
            logger.AddToStdOut(line);

            var colonIdx = line.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx <= 0) continue;

            var key = line[..colonIdx].Trim().ToLowerInvariant();
            var value = line[(colonIdx + 1)..].Trim();

            switch (key)
            {
                case "name":
                    break;
                case "summary":
                    details.Description = value;
                    break;
                case "license":
                    details.License = value;
                    break;
                case "homepage":
                    if (Uri.TryCreate(value, UriKind.Absolute, out var homepage))
                        details.HomepageUrl = homepage;
                    break;
                case "origin":
                    details.Publisher = value;
                    break;
                case "url":
                    if (Uri.TryCreate(value, UriKind.Absolute, out var url))
                        details.ManifestUrl = url;
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
        => Array.Empty<Uri>();

    protected override string? GetInstallLocation_UnSafe(IPackage package)
        => $"/var/lib/flatpak/app/{package.Id}";

    protected override IReadOnlyList<string> GetInstallableVersions_UnSafe(IPackage package)
        => throw new InvalidOperationException("Flatpak does not support installing arbitrary versions");
}
