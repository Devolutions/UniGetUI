using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.Classes;
using UniGetUI.PackageEngine.Classes.Serializable;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.Tui.Views.Dialogs;

namespace UniGetUI.Tui.Infrastructure;

/// <summary>
/// Bundle create / open / save / install / script logic, ported from the desktop
/// <c>PackageBundlesPage</c>. The in-memory bundle is owned by <see cref="PackageBundlesLoader.Instance"/>;
/// this type serializes around it and tracks whether the bundle has unsaved changes.
/// </summary>
internal static class TuiBundleService
{
    private static bool _hasUnsavedChanges;

    public static event Action? UnsavedChangesStateChanged;

    public static bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        set
        {
            if (_hasUnsavedChanges == value) return;
            _hasUnsavedChanges = value;
            UnsavedChangesStateChanged?.Invoke();
        }
    }

    public static BundleFormatType DetectFormat(string path) => path.Split('.')[^1].ToLowerInvariant() switch
    {
        "yaml" or "yml" => BundleFormatType.YAML,
        "xml" => BundleFormatType.XML,
        "json" => BundleFormatType.JSON,
        _ => BundleFormatType.UBUNDLE,
    };

    public static async Task<string> CreateBundleJsonAsync(IReadOnlyList<IPackage> unsortedPackages)
    {
        var exportableData = new SerializableBundle();
        var packages = unsortedPackages.ToList();
        packages.Sort((x, y) =>
        {
            if (x.Id != y.Id) return string.Compare(x.Id, y.Id, StringComparison.Ordinal);
            if (x.Name != y.Name) return string.Compare(x.Name, y.Name, StringComparison.Ordinal);
            return x.NormalizedVersion > y.NormalizedVersion ? -1 : 1;
        });

        foreach (var package in packages)
        {
            if (package is Package && !package.Source.IsVirtualManager)
                exportableData.packages.Add(await package.AsSerializableAsync());
            else
                exportableData.incompatible_packages.Add(package.AsSerializable_Incompatible());
        }

        return exportableData.AsJsonString();
    }

    /// <summary>Saves the current bundle (all its packages) to <paramref name="path"/>.</summary>
    public static async Task SaveAsync(string path)
    {
        string json = await CreateBundleJsonAsync(PackageBundlesLoader.Instance.Packages);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, json);
        HasUnsavedChanges = false;
    }

    public static void Clear()
    {
        PackageBundlesLoader.Instance.ClearPackages();
        HasUnsavedChanges = false;
    }

    /// <summary>
    /// Replaces the bundle with the file's contents only after the security report is acknowledged.
    /// </summary>
    public static async Task<(int Count, bool Imported)> OpenFileAsync(
        string path, Func<BundleReport, Task<bool>> acknowledge)
    {
        string content = await File.ReadAllTextAsync(path);
        var result = await AddFromStringAsync(content, DetectFormat(path), acknowledge, replaceExisting: true);
        return result;
    }

    public static async Task<(int Count, bool Imported)> AddFromStringAsync(
        string content, BundleFormatType format, Func<BundleReport, Task<bool>> acknowledge, bool replaceExisting = false)
    {
        if (format is BundleFormatType.YAML)
            content = await SerializationHelpers.YAML_to_JSON(content);
        else if (format is BundleFormatType.XML)
            content = await SerializationHelpers.XML_to_JSON(content);

        var bundle = await Task.Run(() => new SerializableBundle(
            JsonNode.Parse(content) ?? throw new JsonException("Could not parse bundle JSON")));

        if ((int)(bundle.export_version * 10) != (int)(SerializableBundle.ExpectedVersion * 10))
            Logger.Warn($"Bundle uses schema version {bundle.export_version}, expected {SerializableBundle.ExpectedVersion}.");

        var report = new BundleReport { IsEmpty = true };
        bool allowCli = BundleImportFilter.CliArgumentsAllowed();
        bool allowPrePost = BundleImportFilter.PrePostCommandsAllowed();

        var packages = new List<IPackage>();
        foreach (var raw in bundle.packages)
        {
            var manager = TuiEngine.FindManager(raw.ManagerName);
            var (sourceName, sourceStatus) = BundleImportFilter.ClassifySource(manager, raw.Source);
            raw.InstallationOptions = BundleImportFilter.Apply(
                ref report,
                new BundleReportSubject(raw.Id, raw.Name, manager?.DisplayName ?? raw.ManagerName, sourceName),
                raw.InstallationOptions, allowCli, allowPrePost,
                manager?.CommandLineIsShellInterpreted ?? false, sourceName, sourceStatus);
            packages.Add(DeserializePackage(raw));
        }

        foreach (var raw in bundle.incompatible_packages)
            packages.Add(new InvalidImportedPackage(raw, NullSource.Instance));

        BundleImportFilter.LogReport(report, "TUI bundle import");
        if (!report.IsEmpty && !await acknowledge(report))
        {
            Logger.Warn("The bundle import was discarded by the user after the security report");
            return (0, false);
        }

        if (report.HasHighSeverityFindings)
            Logger.Warn("The user accepted a bundle carrying high-severity security findings");

        if (replaceExisting)
        {
            PackageBundlesLoader.Instance.ClearPackages();
            HasUnsavedChanges = false;
        }
        await PackageBundlesLoader.Instance.AddPackagesAsync(packages);
        return (packages.Count, true);
    }

    public static string DescribeReport(BundleReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(report.HasHighSeverityFindings
            ? CoreTools.Translate("This bundle changes how packages are installed")
            : CoreTools.Translate("Some packages use non-default install settings"));
        sb.AppendLine();
        foreach (var package in report.Contents.Values.OrderByDescending(p => p.HasHighSeverityFindings)
                     .ThenBy(p => p.Subject.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var subject = package.Subject;
            sb.AppendLine($"{subject.DisplayName} ({subject.Id}, {subject.ManagerName}, {subject.Source})");
            foreach (BundleReportEntry entry in package.Entries.OrderByDescending(e => e.Severity))
                sb.AppendLine($"  {(entry.Severity is BundleReportSeverity.High ? "[HIGH]" : "[info]")} "
                    + $"{(entry.Allowed ? "[kept]" : "[removed]")} {CoreTools.Translate(entry.Label)}: {entry.Value}");
        }

        return sb.ToString();
    }

    public static async Task<bool> ReviewReportAsync(BundleReport report)
    {
        await TuiPrompts.ShowTextAsync(CoreTools.Translate("Bundle security report"), DescribeReport(report));
        return !report.HasHighSeverityFindings
            || await TuiModal.ShowAsync(new MessageDialog(
                CoreTools.Translate("Bundle security report"),
                CoreTools.Translate("This bundle changes how packages are installed"),
                [CoreTools.Translate("Import anyway"), CoreTools.Translate("Cancel")],
                defaultButton: 1)) is 0;
    }

    public static async Task AddPackagesAsync(IReadOnlyList<IPackage> packages)
    {
        await PackageBundlesLoader.Instance.AddPackagesAsync(packages);
        HasUnsavedChanges = true;
    }

    public static void Remove(IEnumerable<IPackage> packages)
    {
        PackageBundlesLoader.Instance.RemoveRange(packages.ToList());
        HasUnsavedChanges = true;
    }

    /// <summary>Packages that the bundle's install action should act on.</summary>
    public static IReadOnlyList<IPackage> InstallTargets(IEnumerable<IPackage> packages)
        => Settings.Get(Settings.K.InstallInstalledPackagesBundlesPage)
            ? packages.ToList()
            : packages.Where(p => p.Tag is not PackageTag.AlreadyInstalled).ToList();

    /// <summary>Registers each compatible bundle entry and installs it. Returns the number started.</summary>
    public static async Task<int> InstallAsync(IReadOnlyList<IPackage> packages, bool? elevated = null,
        bool? interactive = null, bool? skipHash = null)
    {
        var toInstall = new List<IPackage>();
        foreach (var package in packages)
        {
            if (package is ImportedPackage imported)
                toInstall.Add(await imported.RegisterAndGetPackageAsync());
            else
                Logger.Warn($"Skipping incompatible bundle package Id={package.Id}");
        }

        return await TuiPackageActions.InstallAsync(toInstall, elevated, interactive, skipHash);
    }

    public static IPackage DeserializePackage(SerializablePackage raw)
    {
        IPackageManager? manager = TuiEngine.FindManager(raw.ManagerName);
        IManagerSource? source;
        if (manager?.Capabilities.SupportsCustomSources == true)
        {
            if (raw.Source.Contains(": "))
                raw.Source = raw.Source.Split(": ")[^1];
            source = manager.SourcesHelper?.Factory.GetSourceIfExists(raw.Source);
        }
        else
        {
            source = manager?.DefaultSource;
        }

        if (manager is null || source is null)
            return new InvalidImportedPackage(raw.GetInvalidEquivalent(), NullSource.Instance);

        return new ImportedPackage(raw, manager, source);
    }

    /// <summary>Writes a PowerShell script that installs the bundle's packages outside UniGetUI.</summary>
    public static async Task<int> CreateInstallScriptAsync(string path)
    {
        var names = new List<string>();
        var commands = new List<string>();
        bool forceKill = Settings.Get(Settings.K.KillProcessesThatRefuseToDie);
        foreach (var p in PackageBundlesLoader.Instance.Packages)
        {
            if (p is not ImportedPackage pkg) continue;
            IReadOnlyList<string> param;
            try
            {
                var exported = pkg.installation_options.Copy();
                exported.CustomInstallLocation = InstallOptionsFactory.ExpandPackagePlaceholders(exported.CustomInstallLocation, pkg);
                param = pkg.Manager.OperationHelper.GetStandaloneParameters(pkg, exported, OperationType.Install);
            }
            catch (InvalidOperationException ex)
            {
                Logger.Warn($"Skipping {pkg.Id} in the exported script: {ex.Message}");
                continue;
            }

            names.Add(pkg.Name + " from " + pkg.Manager.DisplayName);
            foreach (var process in pkg.installation_options.KillBeforeOperation)
            {
                if (!CoreTools.IsSafeProcessImageName(process)) continue;
                commands.Add($"taskkill /im \"{process}\"" + (forceKill ? " /f" : ""));
            }

            if (pkg.installation_options.PreInstallCommand != "") commands.Add(pkg.installation_options.PreInstallCommand);
            commands.Add($"{pkg.Manager.Properties.ExecutableFriendlyName} {string.Join(' ', param)}");
            if (pkg.installation_options.PostInstallCommand != "") commands.Add(pkg.installation_options.PostInstallCommand);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, GenerateScript(names, commands));
        return names.Count;
    }

    private static string GenerateScript(IReadOnlyList<string> names, IReadOnlyList<string> commands)
    {
        string list = string.Join('\n', names.Select(x => $"Write-Host {CoreTools.EscapePowerShellSingleQuoted($"  - {x}")}"));
        string commandArray = string.Join(",\n    ", commands.Select(x => $"'{x.Replace("'", "''")}'"));
        return $$"""
            Clear-Host
            Write-Host ""
            Write-Host "========================================================"
            Write-Host "          UniGetUI Package Installer Script"
            Write-Host "        Created with UniGetUI Version {{CoreData.VersionName}}"
            Write-Host "========================================================"
            Write-Host ""
            Write-Host "NOTES:" -ForegroundColor Yellow
            Write-Host "  - The install process will not be as reliable as importing a bundle with UniGetUI. Expect issues and errors." -ForegroundColor Yellow
            Write-Host "  - Packages will be installed with the install options specified at the time of creation of this script." -ForegroundColor Yellow
            Write-Host "  - You can skip confirmation prompts by running this script with the parameter `/DisablePausePrompts` " -ForegroundColor Yellow
            Write-Host ""
            if ($args[0] -ne "/DisablePausePrompts") { pause }
            Write-Host "This script will attempt to install the following packages:"
            {{list}}
            Write-Host ""
            if ($args[0] -ne "/DisablePausePrompts") { pause }
            Clear-Host

            $success_count=0
            $failure_count=0
            $commands_run=0

            $commands= @(
                {{commandArray}}
            )

            foreach ($command in $commands) {
                Write-Host "Running: $command" -ForegroundColor Yellow
                cmd.exe /C $command
                if ($LASTEXITCODE -eq 0) {
                    Write-Host "[  OK  ] $command" -ForegroundColor Green
                    $success_count++
                }
                else {
                    Write-Host "[ FAIL ] $command" -ForegroundColor Red
                    $failure_count++
                }
                $commands_run++
            }

            Write-Host "Total commands run: $commands_run  Successful: $success_count  Failed: $failure_count"
            if ($args[0] -ne "/DisablePausePrompts") { pause }
            exit $failure_count
            """;
    }
}
