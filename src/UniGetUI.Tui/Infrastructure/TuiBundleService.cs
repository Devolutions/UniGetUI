using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Serializable;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageOperations;

namespace UniGetUI.Tui.Infrastructure;

/// <summary>
/// Bundle create / import / export / restore logic for the TUI. This mirrors the desktop Avalonia
/// <c>PackageBundlesPage</c> static helpers but lives here because the TUI does not (and should not)
/// reference the Avalonia UI project. The in-memory bundle is owned by the shared
/// <see cref="PackageBundlesLoader.Instance"/>; this type only serializes/deserializes around it.
/// </summary>
internal static class TuiBundleService
{
    public static BundleFormatType DetectFormat(string path) => path.Split('.')[^1].ToLowerInvariant() switch
    {
        "yaml" => BundleFormatType.YAML,
        "xml" => BundleFormatType.XML,
        "json" => BundleFormatType.JSON,
        _ => BundleFormatType.UBUNDLE,
    };

    /// <summary>Serializes the given packages into the canonical (JSON/UBUNDLE) bundle string.</summary>
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

    /// <summary>Writes the current bundle contents to <paramref name="path"/> as JSON/UBUNDLE.</summary>
    public static async Task SaveToFileAsync(string path, IReadOnlyList<IPackage> packages)
    {
        string json = await CreateBundleJsonAsync(packages);
        await File.WriteAllTextAsync(path, json);
    }

    /// <summary>
    /// Reads a bundle file, deserializes it, and merges the packages into the shared bundle loader.
    /// Returns the number of packages added. Honors the same security gating as the desktop app
    /// (custom CLI args and pre/post-operation commands are stripped unless explicitly allowed).
    /// </summary>
    public static async Task<int> ImportFileAsync(string path)
    {
        var format = DetectFormat(path);
        string content = await File.ReadAllTextAsync(path);

        if (format is BundleFormatType.YAML)
            content = await SerializationHelpers.YAML_to_JSON(content);
        else if (format is BundleFormatType.XML)
            content = await SerializationHelpers.XML_to_JSON(content);

        var bundle = await Task.Run(() => new SerializableBundle(
            JsonNode.Parse(content) ?? throw new JsonException("Could not parse bundle JSON")));

        bool allowCli = SecureSettings.Get(SecureSettings.K.AllowCLIArguments)
                        && SecureSettings.Get(SecureSettings.K.AllowImportingCLIArguments);
        bool allowPrePost = SecureSettings.Get(SecureSettings.K.AllowPrePostOpCommand)
                            && SecureSettings.Get(SecureSettings.K.AllowImportPrePostOpCommands);

        var packages = new List<IPackage>();
        foreach (var raw in bundle.packages)
        {
            if (!allowCli)
            {
                raw.InstallationOptions.CustomParameters_Install.Clear();
                raw.InstallationOptions.CustomParameters_Update.Clear();
                raw.InstallationOptions.CustomParameters_Uninstall.Clear();
            }
            if (!allowPrePost)
            {
                var o = raw.InstallationOptions;
                o.PreInstallCommand = o.PostInstallCommand = string.Empty;
                o.PreUpdateCommand = o.PostUpdateCommand = string.Empty;
                o.PreUninstallCommand = o.PostUninstallCommand = string.Empty;
            }
            packages.Add(DeserializePackage(raw));
        }

        foreach (var raw in bundle.incompatible_packages)
            packages.Add(DeserializeIncompatiblePackage(raw, NullSource.Instance));

        await PackageBundlesLoader.Instance.AddPackagesAsync(packages);
        return packages.Count;
    }

    /// <summary>
    /// Registers every compatible (<see cref="ImportedPackage"/>) package in the supplied set and enqueues
    /// an install operation for each through <see cref="TuiOperationRegistry"/>. Incompatible packages are
    /// skipped. Returns the number of operations enqueued. M4 installs non-interactively (no elevation).
    /// </summary>
    public static async Task<int> RestoreAsync(IReadOnlyList<IPackage> packages)
    {
        // Simulation mode (UNIGETUI_TUI_SIMULATE=1): enqueue a fake install per compatible package
        // through the same registry path. No registration, no process, no system change.
        if (SimulatedOperation.IsEnabled)
        {
            int simulated = 0;
            foreach (var package in packages)
            {
                if (package is not ImportedPackage) { Logger.Warn($"Skipping incompatible bundle package Id={package.Id}"); continue; }
                TuiOperationRegistry.Start(new SimulatedOperation("Install", package.Name));
                simulated++;
            }
            return simulated;
        }

        var toInstall = new List<Package>();
        foreach (var package in packages)
        {
            if (package is ImportedPackage imported)
                toInstall.Add(await imported.RegisterAndGetPackageAsync());
            else
                Logger.Warn($"Skipping incompatible bundle package Id={package.Id}");
        }

        foreach (var pkg in toInstall)
        {
            var opts = await PackageEngine.PackageClasses.InstallOptionsFactory
                .LoadApplicableAsync(pkg, elevated: false, interactive: false);
            TuiOperationRegistry.Start(new InstallPackageOperation(pkg, opts));
        }

        return toInstall.Count;
    }

    // ─── Deserialization helpers (faithful port of the desktop implementation) ──────────────────
    public static IPackage DeserializePackage(SerializablePackage raw)
    {
        IPackageManager? manager = null;
        foreach (var m in PEInterface.Managers)
        {
            if (m.Id == raw.ManagerName || m.Name == raw.ManagerName || m.DisplayName == raw.ManagerName)
            {
                manager = m;
                break;
            }
        }

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
            return DeserializeIncompatiblePackage(raw.GetInvalidEquivalent(), NullSource.Instance);

        return new ImportedPackage(raw, manager, source);
    }

    public static IPackage DeserializeIncompatiblePackage(SerializableIncompatiblePackage raw, IManagerSource source)
        => new InvalidImportedPackage(raw, source);
}
