using System.Diagnostics;
using System.Text.Json.Nodes;
using UniGetUI.Core.IconEngine;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.PackageEngine.Managers.HomebrewManager;

internal sealed class HomebrewPkgDetailsHelper : BasePkgDetailsHelper
{
    public HomebrewPkgDetailsHelper(Homebrew manager)
        : base(manager) { }

    protected override void GetDetails_UnSafe(IPackageDetails details)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Manager.Status.ExecutablePath,
                Arguments = $"info --json=v2 {details.Package.Id}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        IProcessTaskLogger logger = Manager.TaskLogger.CreateNew(
            Enums.LoggableTaskType.LoadPackageDetails, p);
        p.Start();
        string json = p.StandardOutput.ReadToEnd();
        logger.AddToStdOut(json);
        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();

        if (JsonNode.Parse(json) is not JsonObject root)
        {
            logger.Close(1);
            throw new InvalidOperationException("Failed to parse brew info JSON");
        }

        // Try formula first, then cask
        var formula = root["formulae"]?.AsArray().FirstOrDefault();
        var cask = root["casks"]?.AsArray().FirstOrDefault();

        if (formula is JsonObject f)
            _populateFromFormula(details, f);
        else if (cask is JsonObject c)
            _populateFromCask(details, c);

        logger.Close(0);
    }

    private static void _populateFromFormula(IPackageDetails details, JsonObject f)
    {
        details.Description = f["desc"]?.ToString();
        details.License = f["license"]?.ToString();
        details.InstallerType = "Homebrew Formula";

        if (Uri.TryCreate(f["homepage"]?.ToString(), UriKind.Absolute, out var homepage))
        {
            details.HomepageUrl = homepage;
            details.Author = homepage.Host.Split('.')[^2];
        }

        if (Uri.TryCreate(
                f["urls"]?["stable"]?["url"]?.ToString(),
                UriKind.Absolute, out var url))
            details.InstallerUrl = url;

        // Dependencies
        details.Dependencies.Clear();
        foreach (var dep in f["dependencies"]?.AsArray() ?? [])
        {
            var name = dep?.ToString();
            if (name is not null)
                details.Dependencies.Add(new() { Name = name, Version = "", Mandatory = true });
        }
        foreach (var dep in f["recommended_dependencies"]?.AsArray() ?? [])
        {
            var name = dep?.ToString();
            if (name is not null)
                details.Dependencies.Add(new() { Name = name, Version = "", Mandatory = false });
        }
    }

    private static void _populateFromCask(IPackageDetails details, JsonObject c)
    {
        details.Description = c["desc"]?.ToString();
        details.InstallerType = "Homebrew Cask";

        if (Uri.TryCreate(c["homepage"]?.ToString(), UriKind.Absolute, out var homepage))
        {
            details.HomepageUrl = homepage;
            details.Author = homepage.Host.Split('.')[^2];
        }

        if (Uri.TryCreate(c["url"]?.ToString(), UriKind.Absolute, out var url))
            details.InstallerUrl = url;
    }

    protected override CacheableIcon? GetIcon_UnSafe(IPackage package)
        => throw new NotImplementedException();

    protected override IReadOnlyList<Uri> GetScreenshots_UnSafe(IPackage package)
        => throw new NotImplementedException();

    protected override string? GetInstallLocation_UnSafe(IPackage package)
    {
        // Formulae: /opt/homebrew/Cellar/<name>  (Apple Silicon)
        //           /usr/local/Cellar/<name>       (Intel)
        foreach (var prefix in new[] { "/opt/homebrew", "/usr/local" })
        {
            var path = Path.Join(prefix, "Cellar", package.Id);
            if (Directory.Exists(path))
                return path;
        }
        return null;
    }

    protected override IReadOnlyList<string> GetInstallableVersions_UnSafe(IPackage package)
        => throw new InvalidOperationException("Homebrew does not support installing arbitrary versions");
}
