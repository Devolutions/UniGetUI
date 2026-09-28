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
    /// Replaces the bundle with the file's contents. Returns the security report for any stripped
    /// custom arguments or pre/post commands, as the desktop app shows after opening a bundle.
    /// </summary>
    public static async Task<(int Count, BundleReport Report)> OpenFileAsync(string path)
    {
        string content = await File.ReadAllTextAsync(path);
        PackageBundlesLoader.Instance.ClearPackages();
        var result = await AddFromStringAsync(content, DetectFormat(path));
        HasUnsavedChanges = false;
        return result;
    }

    public static async Task<(int Count, BundleReport Report)> AddFromStringAsync(string content, BundleFormatType format)
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
            raw.InstallationOptions = BundleImportFilter.Apply(ref report, raw.Id, raw.InstallationOptions,
                allowCli, allowPrePost, TuiEngine.FindManager(raw.ManagerName)?.CommandLineIsShellInterpreted ?? false);
            packages.Add(DeserializePackage(raw));
        }

        foreach (var raw in bundle.incompatible_packages)
            packages.Add(new InvalidImportedPackage(raw, NullSource.Instance));

        await PackageBundlesLoader.Instance.AddPackagesAsync(packages);
        return (packages.Count, report);
    }

    public static string DescribeReport(BundleReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CoreTools.Translate("Some of the settings in this bundle were not applied because of your security settings:"));
        sb.AppendLine();
        foreach (var (id, entries) in report.Contents)
        {
            sb.AppendLine(id);
            foreach (BundleReportEntry entry in entries)
                sb.AppendLine($"  {(entry.Allowed ? "[kept]   " : "[removed]")} {entry.Line}");
        }

        return sb.ToString();
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
