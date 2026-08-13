using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.PackageEngine.RemoteHosts;

public static class RemoteInventoryPackageFactory
{
    public static IPackage Create(RemoteInventoryPackageDto dto, Guid hostId)
    {
        RemotePackageManager manager = RemotePackageManager.For(dto.ManagerId);
        IManagerSource source = manager.DefaultSource;
        string name = string.IsNullOrWhiteSpace(dto.Name) ? dto.Id : dto.Name;
        string version = dto.Version;
        string newVersion = string.IsNullOrWhiteSpace(dto.NewVersion) ? version : dto.NewVersion;

        if (dto.IsUpgradable && newVersion != version)
        {
            return new Package(name, dto.Id, version, newVersion, source, manager, remoteHostId: hostId);
        }

        return new Package(name, dto.Id, version, source, manager, remoteHostId: hostId);
    }

    public static RemoteInventoryPackageDto ToDto(IPackage package)
    {
        return new RemoteInventoryPackageDto
        {
            ManagerId = package.Manager.Id,
            Id = package.Id,
            Name = package.Name,
            Version = package.VersionString,
            NewVersion = package.IsUpgradable ? package.NewVersionString : null,
            Source = package.Source.Name,
            IsUpgradable = package.IsUpgradable,
            CanRunAsAdmin = package.Manager.Capabilities.CanRunAsAdmin,
        };
    }
}
