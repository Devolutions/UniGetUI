using Microsoft.Management.Deployment;
using Microsoft.Win32;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;
using Architecture = UniGetUI.PackageEngine.Enums.Architecture;
using InstallOptions = UniGetUI.PackageEngine.Serializable.InstallOptions;

namespace UniGetUI.PackageEngine.Managers.WingetManager;

internal sealed class WinGetPkgOperationHelper : BasePkgOperationHelper
{
    public static string GetIdNamePiece(IPackage package)
    {
        if (!package.Id.EndsWith("…"))
            return $"--id \"{package.Id.TrimEnd('…')}\" --exact";

        if (!package.Name.EndsWith("…"))
            return $"--name \"{package.Name}\" --exact";

        return $"--id \"{package.Id.TrimEnd('…')}\"";
    }

    public WinGetPkgOperationHelper(WinGet manager)
        : base(manager) { }

    protected override IReadOnlyList<string> _getOperationParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation
    )
    {
        List<string> parameters =
        [
            operation switch
            {
                OperationType.Install => Manager.Properties.InstallVerb,
                OperationType.Update => Manager.Properties.UpdateVerb,
                OperationType.Uninstall => Manager.Properties.UninstallVerb,
                _ => throw new InvalidDataException("Invalid package operation"),
            },
        ];

        parameters.AddRange(GetIdNamePiece(package).Split(" "));
        if (!package.Source.IsVirtualManager)
        {
            parameters.AddRange(["--source", package.Source.Name]);
        }
        parameters.AddRange(["--accept-source-agreements", "--disable-interactivity"]);

        // package.OverridenInstallationOptions.Scope is meaningless in WinGet packages. Default is unspecified, hence the _ => [].
        parameters.AddRange(
            (package.OverridenOptions.Scope ?? options.InstallationScope) switch
            {
                PackageScope.User => ["--scope", "user"],
                PackageScope.Machine => ["--scope", "machine"],
                _ => [],
            }
        );

        if (
            operation is OperationType.Uninstall
            && package.VersionString != "Unknown"
            && package.OverridenOptions.WinGet_SpecifyVersion is not false
        )
        {
            parameters.AddRange(["--version", $"\"{package.VersionString}\""]);
        }
        else if (operation is OperationType.Install && options.Version != "")
        {
            parameters.AddRange(["--version", $"\"{options.Version}\""]);
        }

        parameters.Add(options.InteractiveInstallation ? "--interactive" : "--silent");

        if (operation is OperationType.Update)
        {
            if (package.Name.Contains("64-bit") || package.Id.ToLower().Contains("x64"))
            {
                options.Architecture = Architecture.x64;
            }
            else if (package.Name.Contains("32-bit") || package.Id.ToLower().Contains("x86"))
            {
                options.Architecture = Architecture.x86;
            }
            parameters.Add("--include-unknown");

            if (options.CustomInstallLocation != "")
            {
                if (Settings.Get(Settings.K.WinGetForceLocationOnUpdate))
                    parameters.AddRange(["--location", $"\"{options.CustomInstallLocation}\""]);
            }
            else
            {
                var detectedLocation = TryGetPortableInstallLocation(package);
                if (detectedLocation is not null)
                    parameters.AddRange(["--location", $"\"{detectedLocation}\""]);
            }
        }
        else if (operation is OperationType.Install)
        {
            if (options.CustomInstallLocation != "")
                parameters.AddRange(["--location", $"\"{options.CustomInstallLocation}\""]);
        }

        if (operation is not OperationType.Uninstall)
        {
            parameters.AddRange(["--accept-package-agreements", "--force"]);

            if (options.SkipHashCheck)
                parameters.Add("--ignore-security-hash");

            parameters.AddRange(
                options.Architecture switch
                {
                    Architecture.x86 => ["--architecture", "x86"],
                    Architecture.x64 => ["--architecture", "x64"],
                    Architecture.arm64 => ["--architecture", "arm64"],
                    _ => [],
                }
            );
        }

        try
        {
            var installOptions = NativePackageHandler.GetInstallationOptions(
                package,
                options,
                operation
            );
            if (
                installOptions?.ElevationRequirement
                is ElevationRequirement.ElevationRequired
                    or ElevationRequirement.ElevatesSelf
            )
            {
                Logger.Info(
                    $"WinGet package {package.Id} requires elevation, forcing administrator rights..."
                );
                package.OverridenOptions.RunAsAdministrator = true;
            }
            else if (
                installOptions?.ElevationRequirement is ElevationRequirement.ElevationProhibited
            )
            {
                if (CoreTools.IsAdministrator())
                    throw new UnauthorizedAccessException(
                        CoreTools.Translate(
                            "This package cannot be installed from an elevated context."
                        )
                            + CoreTools.Translate(
                                "Please run UniGetUI as a regular user and try again."
                            )
                    );

                if (options.RunAsAdministrator)
                    throw new UnauthorizedAccessException(
                        CoreTools.Translate(
                            "This package cannot be installed from an elevated context."
                        )
                            + CoreTools.Translate(
                                "Please check the installation options for this package and try again"
                            )
                    );

                package.OverridenOptions.RunAsAdministrator = false;
            }
            else if (
                installOptions?.Scope is PackageInstallerScope.System /* or PackageInstallerScope.Unknown*/
            )
            {
                Logger.Info(
                    $"WinGet package {package.Id} is installed on a system-wide scope, forcing administrator rights..."
                );
                package.OverridenOptions.RunAsAdministrator = true;
            }
        }
        catch (Exception ex)
        {
            if (ex is UnauthorizedAccessException)
                throw;

            Logger.Error("Recovered from fatal WinGet exception:");
            Logger.Error(ex);
        }

        parameters.Add(WinGet.GetProxyArgument());

        parameters.AddRange(
            operation switch
            {
                OperationType.Update => options.CustomParameters_Update,
                OperationType.Uninstall => options.CustomParameters_Uninstall,
                _ => options.CustomParameters_Install,
            }
        );
        return parameters;
    }

    protected override OperationVeredict _getOperationResult(
        IPackage package,
        OperationType operation,
        IReadOnlyList<string> processOutput,
        int returnCode
    )
    {
        // See https://github.com/microsoft/winget-cli/blob/master/doc/windows/package-manager/winget/returnCodes.md for reference
        uint uintCode = (uint)returnCode;

        if (uintCode is 0x8A150109)
        { // TODO: Restart required to finish installation
            if (operation is OperationType.Update or OperationType.Install)
                MarkUpgradeAsDone(package);
            return OperationVeredict.Success;
        }

        if (uintCode is 0x8A150077 or 0x8A15010C or 0x8A150005)
        { // At some point, the user clicked cancel or Ctrl+C
            return OperationVeredict.Canceled;
        }

        if (
            operation is OperationType.Uninstall
            && uintCode is 0x8A150017
            && package.OverridenOptions.WinGet_SpecifyVersion is not false
        )
        { // No manifest found matching criteria
            package.OverridenOptions.WinGet_SpecifyVersion = false;
            return OperationVeredict.AutoRetry;
        }

        if (uintCode is 0x8A150011)
        { // TODO: Integrity failed
            return OperationVeredict.Failure;
        }

        if (uintCode is 0x8A15002B)
        {
            //if (Settings.Get(Settings.K.IgnoreUpdatesNotApplicable))
            //{
            Logger.Warn(
                $"Ignoring update {package.Id} as the update is not applicable to the platform, and the user has enabled IgnoreUpdatesNotApplicable"
            );
            IgnoredUpdatesDatabase.Add(
                IgnoredUpdatesDatabase.GetIgnoredIdForPackage(package),
                package.VersionString
            );
            return OperationVeredict.Success;
            //}
            //return OperationVeredict.Failure;
        }

        if (uintCode is 0x8A15010D or 0x8A15004F or 0x8A15010E)
        { // Application is already installed
            if (operation is OperationType.Update or OperationType.Install)
                MarkUpgradeAsDone(package);
            return OperationVeredict.Success;
        }

        if (returnCode is 0)
        { // Operation succeeded
            if (operation is OperationType.Update or OperationType.Install)
                MarkUpgradeAsDone(package);
            return OperationVeredict.Success;
        }

        if (
            uintCode is 0x8A150056
            && package.OverridenOptions.RunAsAdministrator is not false
            && !CoreTools.IsAdministrator()
        )
        { // Installer can't run elevated, but this condition hasn't been forced on UniGetUI
            package.OverridenOptions.RunAsAdministrator = false;
            return OperationVeredict.AutoRetry;
        }

        if (
            (uintCode is 0x8A150019 or 0x80073D28)
            && package.OverridenOptions.RunAsAdministrator is not true
        )
        { // Installer needs to run elevated, handle autoelevation
            // Code 0x80073D28 was added after https://github.com/Devolutions/UniGetUI/issues/3093
            package.OverridenOptions.RunAsAdministrator = true;
            return OperationVeredict.AutoRetry;
        }

        if (
            operation is OperationType.Uninstall
            && (uintCode is 0x8A150030)
            && package.OverridenOptions.RunAsAdministrator is not true
        )
        { // Sometimes, when uninstalling, error code 0x8A150030 can be caused by missing permissions.
            package.OverridenOptions.RunAsAdministrator = true;
            return OperationVeredict.AutoRetry;
        }

        return OperationVeredict.Failure;
    }

    private static void MarkUpgradeAsDone(IPackage package)
    {
        var options = InstallOptionsFactory.LoadApplicable(package);
        string version;
        if (package.IsUpgradable)
            version = package.NewVersionString;
        else if (options.Version != "")
            version = options.Version;
        else
            version = package.VersionString;
        Settings.SetDictionaryItem<string, string>(
            Settings.K.WinGetAlreadyUpgradedPackages,
            package.Id,
            version
        );
    }

    public static bool UpdateAlreadyInstalled(IPackage package)
    {
        return Settings.GetDictionaryItem<string, string>(
                Settings.K.WinGetAlreadyUpgradedPackages,
                package.Id
            ) == package.NewVersionString;
    }

    public static string GetLastInstalledVersion(string id)
    {
        var val = Settings.GetDictionaryItem<string, string>(
            Settings.K.WinGetAlreadyUpgradedPackages,
            id
        );
        if (val is null || val == "")
            val = "Unknown";
        return val;
    }

    /// <summary>
    /// For portable WinGet packages, reads the current install location from the Windows registry
    /// ARP entry (written by WinGet at install time). Returns null if the package is not portable
    /// or the location cannot be determined.
    /// </summary>
    private static string? TryGetPortableInstallLocation(IPackage package)
    {
        try
        {
            foreach (
                var hive in new RegistryKey[] { Registry.CurrentUser, Registry.LocalMachine }
            )
            {
                using var uninstallKey = hive.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall"
                );
                if (uninstallKey is null)
                    continue;

                foreach (var name in uninstallKey.GetSubKeyNames())
                {
                    using var entry = uninstallKey.OpenSubKey(name);
                    if (entry is null)
                        continue;

                    if (
                        entry.GetValue("WinGetPackageIdentifier") is not string pkgId
                        || !string.Equals(pkgId, package.Id, StringComparison.OrdinalIgnoreCase)
                    )
                        continue;

                    if (
                        entry.GetValue("WinGetInstallerType") is not string installerType
                        || !string.Equals(
                            installerType,
                            "portable",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        return null;

                    if (
                        entry.GetValue("InstallLocation") is string location
                        && location.Length > 0
                    )
                    {
                        Logger.Info(
                            $"Auto-detected portable install location for {package.Id}: {location}"
                        );
                        return location;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(
                $"Failed to auto-detect portable install location for {package.Id}: {ex.Message}"
            );
        }

        return null;
    }
}
