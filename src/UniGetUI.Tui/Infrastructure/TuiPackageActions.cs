using System.Text;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageOperations;
using UniGetUI.Tui.FakeData;

namespace UniGetUI.Tui.Infrastructure;

/// <summary>
/// The package operation launchers, ported from the desktop pages (<c>PackagesPageViewModel.LaunchInstall</c>,
/// <c>SoftwareUpdatesPage.LaunchUpdate</c>, <c>InstalledPackagesPage.LaunchUninstall</c>, …). Each follows
/// the same sequence: load the applicable options with the requested overrides, skip packages that
/// already have a pending operation of that kind, build the engine operation and hand it to
/// <see cref="TuiOperationRegistry"/>.
/// </summary>
internal static class TuiPackageActions
{
    public static async Task<int> InstallAsync(IEnumerable<IPackage> packages, bool? elevated = null,
        bool? interactive = null, bool? skipHash = null)
    {
        int started = 0;
        foreach (IPackage pkg in packages)
        {
            if (pkg.Source.IsVirtualManager) continue;
            var opts = await InstallOptionsFactory.LoadApplicableAsync(pkg, elevated: elevated,
                interactive: interactive, no_integrity: skipHash);
            if (PackageOperation.HasPendingOperation(pkg, OperationType.Install)) continue;
            TuiOperationRegistry.Start(new InstallPackageOperation(pkg, opts));
            started++;
        }

        return started;
    }

    public static async Task<int> UpdateAsync(IEnumerable<IPackage> packages, bool? elevated = null,
        bool? interactive = null, bool? skipHash = null)
    {
        int started = 0;
        foreach (IPackage pkg in packages)
        {
            var opts = await InstallOptionsFactory.LoadApplicableAsync(pkg, elevated: elevated,
                interactive: interactive, no_integrity: skipHash);
            if (PackageOperation.HasPendingOperation(pkg, OperationType.Update)) continue;
            TuiOperationRegistry.Start(new UpdatePackageOperation(pkg, opts));
            started++;
        }

        return started;
    }

    public static async Task<int> UninstallAsync(IEnumerable<IPackage> packages, bool? elevated = null,
        bool? interactive = null, bool? removeData = null)
    {
        int started = 0;
        foreach (IPackage pkg in packages)
        {
            var opts = await InstallOptionsFactory.LoadApplicableAsync(pkg, elevated: elevated,
                interactive: interactive, remove_data: removeData);
            if (PackageOperation.HasPendingOperation(pkg, OperationType.Uninstall)) continue;
            TuiOperationRegistry.Start(new UninstallPackageOperation(pkg, opts));
            started++;
        }

        return started;
    }

    /// <summary>Installed page "Update to version X": updates through the matching upgradable package.</summary>
    public static async Task<bool> UpdateInstalledAsync(IPackage installed, bool? elevated = null)
    {
        if (installed.GetUpgradablePackage() is not { } upgradable) return false;
        return await UpdateAsync([upgradable], elevated: elevated) > 0;
    }

    public static async Task<bool> ReinstallAsync(IPackage package)
    {
        if (package.Source.IsVirtualManager) return false;
        var opts = await InstallOptionsFactory.LoadApplicableAsync(package);
        if (PackageOperation.HasPendingOperation(package, OperationType.Install)) return false;
        TuiOperationRegistry.Start(new InstallPackageOperation(package, opts));
        return true;
    }

    /// <summary>
    /// Uninstall, then install fresh (used by "Uninstall then reinstall" and "Uninstall then update").
    /// The uninstall runs as the install's prerequisite, so only the install is started.
    /// </summary>
    public static async Task<bool> UninstallThenInstallAsync(IPackage package)
    {
        if (package.Source.IsVirtualManager) return false;
        var uninstallOpts = await InstallOptionsFactory.LoadApplicableAsync(package);
        var installOpts = await InstallOptionsFactory.LoadApplicableAsync(package);
        if (PackageOperation.HasPendingOperation(package, OperationType.Install)) return false;
        var uninstall = new UninstallPackageOperation(package, uninstallOpts);
        var install = new InstallPackageOperation(package, installOpts, req: uninstall);
        TuiOperationRegistry.Track(uninstall);
        TuiOperationRegistry.Start(install);
        return true;
    }

    public static Task<int> UpdateAllAsync()
        => UpdateAsync(UpgradablePackagesLoader.Instance.Packages.ToList());

    public static Task<int> UpdateAllForManagerAsync(IPackageManager manager)
        => UpdateAsync(UpgradablePackagesLoader.Instance.Packages.Where(p => p.Manager == manager).ToList());

    /// <summary>Downloads installers into <paramref name="folderOrFile"/> for every eligible package.</summary>
    public static int Download(IEnumerable<IPackage> packages, string folderOrFile)
    {
        int started = 0;
        foreach (IPackage pkg in packages)
        {
            if (pkg.Source.IsVirtualManager || !pkg.Manager.Capabilities.CanDownloadInstaller) continue;
            AbstractOperation op = TuiEngine.IsFakeData
                ? new FakeDownloadOperation(pkg, folderOrFile)
                : new DownloadOperation(pkg, folderOrFile);
            TuiOperationRegistry.Start(op);
            started++;
        }

        return started;
    }

    public static string DefaultDownloadDirectory()
    {
        try
        {
            string? dir = InstallerDownloadLocation.ResolveStartDirectory();
            if (!string.IsNullOrWhiteSpace(dir)) return dir;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex);
        }

        return UniGetUI.Core.Data.CoreData.UniGetUIDataDirectory;
    }

    /// <summary>The standalone command a user could type to run this operation by hand.</summary>
    public static async Task<string?> BuildManualCommandAsync(IPackage package, OperationType operation)
    {
        if (package.Source.IsVirtualManager) return null;
        var options = await InstallOptionsFactory.LoadApplicableAsync(package);
        try
        {
            var args = await Task.Run(() =>
                package.Manager.OperationHelper.GetStandaloneParameters(package, options, operation));
            return package.Manager.Properties.ExecutableFriendlyName + " " + string.Join(' ', args);
        }
        catch (InvalidOperationException ex)
        {
            Logger.Warn($"No command line for {package.Id}: {ex.Message}");
            return null;
        }
    }

    // ─── Ignored updates ────────────────────────────────────────────────────

    public static async Task IgnoreUpdatesAsync(IPackage package, string version = "*")
    {
        await package.AddToIgnoredUpdatesAsync(version);
        UpgradablePackagesLoader.Instance.IgnoredPackages[package.Id] = package;
        UpgradablePackagesLoader.Instance.Remove(package);
    }

    public static Task SkipVersionAsync(IPackage package) => IgnoreUpdatesAsync(package, package.NewVersionString);

    public static Task PauseUpdatesAsync(IPackage package, IgnoredUpdatesDatabase.PauseTime time)
        => IgnoreUpdatesAsync(package, "<" + time.GetDateFromNow());

    public static IReadOnlyList<IgnoredUpdatesDatabase.PauseTime> PauseDurations { get; } =
    [
        new() { Days = 1 }, new() { Days = 3 }, new() { Weeks = 1 }, new() { Weeks = 2 },
        new() { Weeks = 4 }, new() { Months = 3 }, new() { Months = 6 }, new() { Months = 12 },
    ];

    /// <summary>Installed page toggle: ignore or stop ignoring this package's updates.</summary>
    public static async Task<bool> ToggleIgnoreUpdatesAsync(IPackage package)
    {
        if (await package.HasUpdatesIgnoredAsync())
        {
            await package.RemoveFromIgnoredUpdatesAsync();
            return false;
        }

        await package.AddToIgnoredUpdatesAsync();
        if (package.GetUpgradablePackage() is { } upgradable)
        {
            UpgradablePackagesLoader.Instance.IgnoredPackages[upgradable.Id] = upgradable;
            UpgradablePackagesLoader.Instance.Remove(upgradable);
        }

        return true;
    }

    // ─── Export ─────────────────────────────────────────────────────────────

    /// <summary>CSV export matching the desktop app: formula-injection safe, UTF-8 with BOM.</summary>
    public static string BuildCsv(IEnumerable<IPackage> packages, bool includeNewVersion)
    {
        var sb = new StringBuilder();
        sb.AppendLine(includeNewVersion
            ? "Name,Id,Installed version,New version,Source,Manager"
            : "Name,Id,Version,Source,Manager");
        foreach (IPackage p in packages)
        {
            var cells = new List<string> { p.Name, p.Id, p.VersionString };
            if (includeNewVersion) cells.Add(p.NewVersionString);
            cells.Add(p.Source.AsString_DisplayName);
            cells.Add(p.Manager.DisplayName);
            sb.AppendLine(string.Join(',', cells.Select(Escape)));
        }

        return sb.ToString();
    }

    public static async Task WriteCsvAsync(string path, IEnumerable<IPackage> packages, bool includeNewVersion)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, BuildCsv(packages, includeNewVersion), new UTF8Encoding(true));
    }

    private static string Escape(string? value)
    {
        value ??= string.Empty;
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    public static string? InstallLocation(IPackage package)
    {
        try
        {
            return package.Manager.DetailsHelper.GetInstallLocation(package);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex);
            return null;
        }
    }

    /// <summary>Opens a folder or URL with the OS shell. Refused in fake-data mode (prints instead).</summary>
    public static void OpenExternally(string target)
    {
        if (TuiEngine.IsFakeData)
        {
            TuiNotifications.Info(CoreTools.Translate("Fake data mode"), $"Would open: {target}");
            return;
        }

        try
        {
            CoreTools.Launch(target);
        }
        catch (Exception ex)
        {
            TuiNotifications.Error(CoreTools.Translate("Could not open"), ex.Message);
        }
    }
}
