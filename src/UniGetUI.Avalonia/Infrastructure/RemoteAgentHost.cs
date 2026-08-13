using System.Runtime.InteropServices;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.RemoteHosts;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class RemoteAgentHost
{
    public static bool IsRemoteCommand(IReadOnlyList<string> args)
    {
        int index = FirstNonOptionIndex(args);
        return index >= 0 && string.Equals(args[index], "remote", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            Dictionary<string, string> options = ParseOptions(args);
            int version = 0;
            if (!options.TryGetValue("protocol", out string? protocol)
                || !int.TryParse(protocol, out version)
                || version != RemoteControlProtocol.Version)
            {
                WriteResponse(new RemoteControlResponse
                {
                    Protocol = version,
                    Ok = false,
                    Message = "incompatible-protocol",
                });
                return 2;
            }

            string verb = GetVerb(args);
            ProcessEnvironmentConfigurator.PrepareForCurrentPlatform();
            PEInterface.LoadLoaders();
            await Task.Run(PEInterface.LoadManagers).ConfigureAwait(false);

            RemoteControlResponse response = verb switch
            {
                "hello" => BuildHello(),
                "inventory" => await InventoryAsync().ConfigureAwait(false),
                "search" => await SearchAsync(options.GetValueOrDefault("query") ?? "").ConfigureAwait(false),
                "update" => await MutateAsync(OperationType.Update, options).ConfigureAwait(false),
                "uninstall" => await MutateAsync(OperationType.Uninstall, options).ConfigureAwait(false),
                "update-all" => await UpdateAllAsync().ConfigureAwait(false),
                _ => new RemoteControlResponse { Ok = false, Message = $"Unknown remote verb '{verb}'." },
            };

            WriteResponse(response);
            return response.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            WriteResponse(new RemoteControlResponse { Ok = false, Message = ex.Message });
            return 1;
        }
    }

    private static RemoteControlResponse BuildHello()
    {
        return new RemoteControlResponse
        {
            Ok = true,
            Backend = "agent",
            Os = CurrentOs(),
            HostDescription = $"{Environment.MachineName} ({RuntimeInformation.OSDescription})",
            CanElevate = CoreTools.IsAdministrator(),
        };
    }

    private static async Task<RemoteControlResponse> InventoryAsync()
    {
        List<IPackage> installed = [];
        List<IPackage> updates = [];
        foreach (IPackageManager manager in PEInterface.Managers)
        {
            if (!manager.IsReady())
                continue;
            installed.AddRange(await Task.Run(manager.GetInstalledPackages).ConfigureAwait(false));
            updates.AddRange(await Task.Run(manager.GetAvailableUpdates).ConfigureAwait(false));
        }

        Dictionary<string, IPackage> updateMap = updates
            .GroupBy(pkg => $"{pkg.Manager.Id}\\{pkg.Id}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        List<RemoteInventoryPackageDto> packages = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (IPackage pkg in installed)
        {
            string key = $"{pkg.Manager.Id}\\{pkg.Id}";
            seen.Add(key);
            if (updateMap.TryGetValue(key, out IPackage? update))
                packages.Add(RemoteInventoryPackageFactory.ToDto(update));
            else
                packages.Add(RemoteInventoryPackageFactory.ToDto(pkg));
        }

        foreach (IPackage pkg in updates)
        {
            string key = $"{pkg.Manager.Id}\\{pkg.Id}";
            if (seen.Add(key))
                packages.Add(RemoteInventoryPackageFactory.ToDto(pkg));
        }

        return new RemoteControlResponse
        {
            Ok = true,
            Backend = "agent",
            Os = CurrentOs(),
            HostDescription = Environment.MachineName,
            CanElevate = CoreTools.IsAdministrator(),
            Packages = packages,
        };
    }

    private static async Task<RemoteControlResponse> SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new RemoteControlResponse { Ok = true, Backend = "agent", Os = CurrentOs(), Packages = [] };

        List<RemoteInventoryPackageDto> packages = [];
        foreach (IPackageManager manager in PEInterface.Managers)
        {
            if (!manager.IsReady())
                continue;
            IReadOnlyList<IPackage> found = await Task.Run(() => manager.FindPackages(query)).ConfigureAwait(false);
            packages.AddRange(found.Take(50).Select(RemoteInventoryPackageFactory.ToDto));
        }

        return new RemoteControlResponse
        {
            Ok = true,
            Backend = "agent",
            Os = CurrentOs(),
            CanElevate = CoreTools.IsAdministrator(),
            Packages = packages,
        };
    }

    private static async Task<RemoteControlResponse> MutateAsync(
        OperationType role,
        Dictionary<string, string> options
    )
    {
        if (!options.TryGetValue("manager", out string? managerId)
            || !options.TryGetValue("id", out string? packageId))
        {
            return new RemoteControlResponse { Ok = false, Message = "Missing --manager or --id." };
        }

        IPackage? package = FindPackage(managerId, packageId);
        if (package is null)
            return new RemoteControlResponse { Ok = false, Message = $"Package {packageId} was not found." };

        InstallOptions opts = await InstallOptionsFactory.LoadApplicableAsync(package).ConfigureAwait(false);
        PackageOperation operation = role is OperationType.Update
            ? new UpdatePackageOperation(package, opts, IgnoreParallelInstalls: true)
            : new UninstallPackageOperation(package, opts, IgnoreParallelInstalls: true);

        await operation.MainThread().ConfigureAwait(false);
        bool ok = operation.Status == OperationStatus.Succeeded;
        RemoteControlResponse inventory = await InventoryAsync().ConfigureAwait(false);
        inventory.Ok = ok;
        if (!ok)
            inventory.Message = $"Remote {role} of {packageId} failed.";
        return inventory;
    }

    private static async Task<RemoteControlResponse> UpdateAllAsync()
    {
        RemoteControlResponse before = await InventoryAsync().ConfigureAwait(false);
        foreach (RemoteInventoryPackageDto dto in before.Packages.Where(pkg => pkg.IsUpgradable))
        {
            IPackage? package = FindPackage(dto.ManagerId, dto.Id);
            if (package is null)
                continue;
            InstallOptions opts = await InstallOptionsFactory.LoadApplicableAsync(package).ConfigureAwait(false);
            var operation = new UpdatePackageOperation(package, opts, IgnoreParallelInstalls: true);
            await operation.MainThread().ConfigureAwait(false);
        }

        return await InventoryAsync().ConfigureAwait(false);
    }

    private static IPackage? FindPackage(string managerId, string packageId)
    {
        IPackageManager? manager = PEInterface.Managers.FirstOrDefault(
            item => item.Id.Equals(managerId, StringComparison.OrdinalIgnoreCase)
        );
        if (manager is null)
            return null;

        return manager.GetAvailableUpdates().FirstOrDefault(pkg => pkg.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase))
            ?? manager.GetInstalledPackages().FirstOrDefault(pkg => pkg.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteResponse(RemoteControlResponse response)
        => Console.Out.WriteLine(RemoteHostsJson.SerializeResponse(response));

    private static string CurrentOs()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS()) return "macos";
        if (OperatingSystem.IsLinux()) return "linux";
        return "unknown";
    }

    private static int FirstNonOptionIndex(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (!args[i].StartsWith('-'))
                return i;
        }
        return -1;
    }

    private static string GetVerb(IReadOnlyList<string> args)
    {
        bool seenRemote = false;
        foreach (string arg in args)
        {
            if (arg.StartsWith('-'))
                continue;
            if (!seenRemote && arg.Equals("remote", StringComparison.OrdinalIgnoreCase))
            {
                seenRemote = true;
                continue;
            }
            if (seenRemote)
                return arg.ToLowerInvariant();
        }
        return "hello";
    }

    private static Dictionary<string, string> ParseOptions(IReadOnlyList<string> args)
    {
        Dictionary<string, string> options = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Count; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Count)
                continue;
            options[args[i][2..]] = args[i + 1];
            i++;
        }
        return options;
    }
}
