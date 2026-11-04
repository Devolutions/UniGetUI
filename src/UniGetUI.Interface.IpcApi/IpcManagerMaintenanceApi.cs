using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
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
    public IpcManagerHealthInfo Health { get; set; } = new();
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

        ManagerMaintenanceAction maintenanceAction = manager.FindAction(action)
            ?? throw new InvalidOperationException(
                $"The manager action \"{request.Action}\" is not supported."
            );

        if (maintenanceAction.RequiresConfirmation)
        {
            EnsureConfirmed(request, action);
        }

        await manager.RunAsync(maintenanceAction);

        return Success(
            "run-manager-action",
            manager,
            action,
            "completed",
            $"The {action} action completed for {manager.DisplayName}."
        );
    }

    private static IpcManagerMaintenanceInfo ToMaintenanceInfo(IPackageManager manager)
    {
        string? configuredExecutablePath = Settings.GetDictionaryItem<string, string>(
            Settings.K.ManagerPaths,
            manager.Name
        );

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
            SupportedActions = manager.GetMaintenanceActions().Select(candidate => candidate.Id).ToArray(),
            Health = IpcManagerHealthInfo.From(manager),
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
                ? Settings.Get(Settings.K.EnableScoopCleanupCache) || Settings.Get(Settings.K.EnableScoopCleanupApps) || Settings.Get(Settings.K.EnableScoopCleanup)
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

    private static void EnsureConfirmed(IpcManagerMaintenanceRequest request, string action)
    {
        if (!request.Confirm)
        {
            throw new InvalidOperationException(
                $"The manager action \"{action}\" requires confirm=true."
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
