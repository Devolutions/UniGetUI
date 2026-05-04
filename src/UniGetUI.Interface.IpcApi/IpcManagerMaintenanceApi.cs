using System.Diagnostics;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.VcpkgManager;

namespace UniGetUI.Interface;

public sealed class IpcManagerMaintenanceInfo
{
    public string Manager { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Ready { get; set; }
    public bool CustomExecutablePathsAllowed { get; set; }
    public string? ConfiguredExecutablePath { get; set; }
    public string EffectiveExecutablePath { get; set; } = "";
    public IReadOnlyList<string> CandidateExecutablePaths { get; set; } = [];
    public IReadOnlyList<string> SupportedActions { get; set; } = [];
    public bool? UseBundledWinGet { get; set; }
    public bool? UseSystemChocolatey { get; set; }
    public bool? ScoopCleanupOnLaunch { get; set; }
    public bool UpdateNotificationsSuppressed { get; set; }
    public string? DefaultVcpkgTriplet { get; set; }
    public IReadOnlyList<string> AvailableVcpkgTriplets { get; set; } = [];
    public string? CustomVcpkgRoot { get; set; }
}

public sealed class IpcManagerMaintenanceRequest
{
    public string ManagerName { get; set; } = "";
    public string? Action { get; set; }
    public string? Path { get; set; }
    public bool Confirm { get; set; }
}

public sealed class IpcManagerMaintenanceActionResult
{
    public string Status { get; set; } = "success";
    public string Command { get; set; } = "";
    public string Manager { get; set; } = "";
    public string Action { get; set; } = "";
    public string OperationStatus { get; set; } = "";
    public string? Message { get; set; }
    public IpcManagerMaintenanceInfo Maintenance { get; set; } = new();
}

public static class IpcManagerMaintenanceApi
{
    public static IpcManagerMaintenanceInfo GetMaintenanceInfo(string managerName)
    {
        return ToMaintenanceInfo(IpcManagerSettingsApi.ResolveManager(managerName));
    }

    public static async Task<IpcManagerMaintenanceActionResult> ReloadManagerAsync(
        IpcManagerMaintenanceRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var manager = IpcManagerSettingsApi.ResolveManager(request.ManagerName);
        await ReloadManagerAsync(manager);
        return Success("reload-manager", manager, "reload", "completed");
    }

    public static async Task<IpcManagerMaintenanceActionResult> SetExecutablePathAsync(
        IpcManagerMaintenanceRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var manager = IpcManagerSettingsApi.ResolveManager(request.ManagerName);
        if (!SecureSettings.Get(SecureSettings.K.AllowCustomManagerPaths))
        {
            throw new InvalidOperationException(
                "Custom manager paths are disabled by secure settings."
            );
        }

        if (string.IsNullOrWhiteSpace(request.Path))
        {
            throw new InvalidOperationException("The path field is required.");
        }

        Settings.SetDictionaryItem(Settings.K.ManagerPaths, manager.Name, request.Path);
        await ReloadManagerAsync(manager);
        return Success(
            "set-manager-executable",
            manager,
            "set-executable",
            "completed",
            $"Configured {manager.DisplayName} to use {request.Path}."
        );
    }

    public static async Task<IpcManagerMaintenanceActionResult> ClearExecutablePathAsync(
        IpcManagerMaintenanceRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var manager = IpcManagerSettingsApi.ResolveManager(request.ManagerName);
        Settings.RemoveDictionaryKey<string, string>(Settings.K.ManagerPaths, manager.Name);
        await ReloadManagerAsync(manager);
        return Success(
            "clear-manager-executable",
            manager,
            "clear-executable",
            "completed",
            $"Cleared the custom executable override for {manager.DisplayName}."
        );
    }

    public static async Task<IpcManagerMaintenanceActionResult> RunActionAsync(
        IpcManagerMaintenanceRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var manager = IpcManagerSettingsApi.ResolveManager(request.ManagerName);
        string action = request.Action?.Trim().ToLowerInvariant()
            ?? throw new InvalidOperationException("The action field is required.");

        switch (action)
        {
            case "repair-winget":
                EnsureConfirmed(request, action);
                EnsureManager(manager, "WinGet");
                EnsureWindowsOnly(action);
                await RunWindowsProcessAsync(
                    CoreData.PowerShell5,
                    "-ExecutionPolicy Bypass -NoLogo -NoProfile -Command \"& {"
                    + "cmd.exe /C \"\"rmdir /Q /S `\"%temp%\\WinGet`\"\"\"; "
                    + "cmd.exe /C \"\"`\"%localappdata%\\Microsoft\\WindowsApps\\winget.exe`\" source reset --force\"\"; "
                    + "taskkill /im winget.exe /f; "
                    + "taskkill /im WindowsPackageManagerServer.exe /f; "
                    + "Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force; "
                    + "Install-Module Microsoft.WinGet.Client -Force -AllowClobber; "
                    + "Import-Module Microsoft.WinGet.Client; "
                    + "Repair-WinGetPackageManager -Force -Latest; "
                    + "Get-AppxPackage -Name 'Microsoft.DesktopAppInstaller' | Reset-AppxPackage; "
                    + "}\"",
                    runAsAdmin: true
                );
                Settings.SetValue(Settings.K.WinGetCliToolPreference, "default");
                await ReloadManagerAsync(manager);
                return Success(
                    "run-manager-action",
                    manager,
                    action,
                    "completed",
                    "WinGet repair completed."
                );

            case "install-scoop":
                EnsureConfirmed(request, action);
                EnsureManager(manager, "Scoop");
                EnsureWindowsOnly(action);
                string installScriptPath = Path.Join(
                    CoreData.UniGetUIExecutableDirectory,
                    "Assets",
                    "Utilities",
                    "install_scoop.ps1"
                );
                await RunWindowsProcessAsync(
                    CoreData.PowerShell5,
                    $"-ExecutionPolicy Bypass -File \"{installScriptPath}\"",
                    runAsAdmin: true
                );
                await ReloadManagerAsync(manager);
                return Success(
                    "run-manager-action",
                    manager,
                    action,
                    "completed",
                    "Scoop installation completed."
                );

            case "uninstall-scoop":
                EnsureConfirmed(request, action);
                EnsureManager(manager, "Scoop");
                EnsureWindowsOnly(action);
                await RunWindowsProcessAsync(
                    CoreData.PowerShell5,
                    "-ExecutionPolicy Bypass -Command \"scoop uninstall -p scoop\""
                );
                await ReloadManagerAsync(manager);
                return Success(
                    "run-manager-action",
                    manager,
                    action,
                    "completed",
                    "Scoop uninstall completed."
                );

            case "cleanup-scoop":
                EnsureConfirmed(request, action);
                EnsureManager(manager, "Scoop");
                EnsureWindowsOnly(action);
                if (string.IsNullOrWhiteSpace(manager.Status.ExecutablePath))
                {
                    throw new InvalidOperationException("Scoop is not ready.");
                }

                await RunWindowsProcessAsync(
                    manager.Status.ExecutablePath,
                    manager.Status.ExecutableCallArgs + " cache rm *"
                );
                await RunWindowsProcessAsync(
                    manager.Status.ExecutablePath,
                    manager.Status.ExecutableCallArgs + " cleanup --all --cache"
                );
                await RunWindowsProcessAsync(
                    manager.Status.ExecutablePath,
                    manager.Status.ExecutableCallArgs + " cleanup --all --global --cache",
                    runAsAdmin: true
                );
                await ReloadManagerAsync(manager);
                return Success(
                    "run-manager-action",
                    manager,
                    action,
                    "completed",
                    "Scoop cleanup completed."
                );

            default:
                throw new InvalidOperationException(
                    $"The manager action \"{request.Action}\" is not supported."
                );
        }
    }

    private static IpcManagerMaintenanceInfo ToMaintenanceInfo(IPackageManager manager)
    {
        string? configuredExecutablePath = Settings.GetDictionaryItem<string, string>(
            Settings.K.ManagerPaths,
            manager.Name
        );

        List<string> supportedActions =
        [
            "reload",
        ];

        if (manager.Name.Equals("WinGet", StringComparison.OrdinalIgnoreCase))
        {
            supportedActions.Add("repair-winget");
        }
        else if (manager.Name.Equals("Scoop", StringComparison.OrdinalIgnoreCase))
        {
            supportedActions.Add("install-scoop");
            supportedActions.Add("uninstall-scoop");
            supportedActions.Add("cleanup-scoop");
        }

        IReadOnlyList<string> triplets = manager.Name.Equals("vcpkg", StringComparison.OrdinalIgnoreCase)
            ? Vcpkg.GetSystemTriplets().ToArray()
            : [];

        string? customVcpkgRoot = manager.Name.Equals("vcpkg", StringComparison.OrdinalIgnoreCase)
            && Settings.Get(Settings.K.CustomVcpkgRoot)
            ? Settings.GetValue(Settings.K.CustomVcpkgRoot)
            : null;

        return new IpcManagerMaintenanceInfo
        {
            Manager = IpcManagerSettingsApi.GetPublicManagerId(manager),
            DisplayName = manager.DisplayName,
            Enabled = manager.IsEnabled(),
            Ready = manager.IsReady(),
            CustomExecutablePathsAllowed = SecureSettings.Get(SecureSettings.K.AllowCustomManagerPaths),
            ConfiguredExecutablePath = string.IsNullOrWhiteSpace(configuredExecutablePath)
                ? null
                : configuredExecutablePath,
            EffectiveExecutablePath = manager.Status.ExecutablePath,
            CandidateExecutablePaths = manager.FindCandidateExecutableFiles().ToArray(),
            SupportedActions = supportedActions,
            UseBundledWinGet = manager.Name.Equals("WinGet", StringComparison.OrdinalIgnoreCase)
                ? string.Equals(
                    Settings.GetValue(Settings.K.WinGetCliToolPreference),
                    "pinget",
                    StringComparison.OrdinalIgnoreCase
                )
                : null,
            UseSystemChocolatey = manager.Name.Equals("Chocolatey", StringComparison.OrdinalIgnoreCase)
                ? true
                : null,
            ScoopCleanupOnLaunch = manager.Name.Equals("Scoop", StringComparison.OrdinalIgnoreCase)
                ? Settings.Get(Settings.K.EnableScoopCleanup)
                : null,
            UpdateNotificationsSuppressed = Settings.GetDictionaryItem<string, bool>(
                Settings.K.DisabledPackageManagerNotifications,
                manager.Name
            ),
            DefaultVcpkgTriplet = manager.Name.Equals("vcpkg", StringComparison.OrdinalIgnoreCase)
                ? Settings.GetValue(Settings.K.DefaultVcpkgTriplet)
                : null,
            AvailableVcpkgTriplets = triplets,
            CustomVcpkgRoot = string.IsNullOrWhiteSpace(customVcpkgRoot) ? null : customVcpkgRoot,
        };
    }

    private static async Task ReloadManagerAsync(IPackageManager manager)
    {
        await Task.Run(manager.Initialize);
    }

    private static async Task RunWindowsProcessAsync(
        string fileName,
        string arguments,
        bool runAsAdmin = false
    )
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = runAsAdmin,
                Verb = runAsAdmin ? "runas" : string.Empty,
                CreateNoWindow = !runAsAdmin,
            },
        };
        process.Start();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The maintenance command exited with code {process.ExitCode}."
            );
        }
    }

    private static void EnsureConfirmed(IpcManagerMaintenanceRequest request, string action)
    {
        if (!request.Confirm)
        {
            throw new InvalidOperationException(
                $"The manager action \"{action}\" requires confirm=true."
            );
        }
    }

    private static void EnsureManager(IPackageManager manager, string expectedName)
    {
        if (!manager.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{expectedName} maintenance actions can only run against the {expectedName} manager."
            );
        }
    }

    private static void EnsureWindowsOnly(string action)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException(
                $"The manager action \"{action}\" is only supported on Windows."
            );
        }
    }

    private static IpcManagerMaintenanceActionResult Success(
        string command,
        IPackageManager manager,
        string action,
        string operationStatus,
        string? message = null
    )
    {
        return new IpcManagerMaintenanceActionResult
        {
            Status = "success",
            Command = command,
            Manager = IpcManagerSettingsApi.GetPublicManagerId(manager),
            Action = action,
            OperationStatus = operationStatus,
            Message = message,
            Maintenance = ToMaintenanceInfo(manager),
        };
    }
}
