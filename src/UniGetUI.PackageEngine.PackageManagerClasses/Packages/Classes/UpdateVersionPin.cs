using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.PackageClasses
{
    public static class UpdateVersionPin
    {
        public static bool HasExplicitVersion(InstallOptions options) =>
            options.VersionIsExplicit && options.Version.Length > 0;

        public static bool HonorsVersionOnUpdate(IPackageManager manager) =>
            manager.Capabilities.SupportsCustomVersionsOnUpdate || manager.CanUpdateToExactVersion;

        public static bool HasHonoredExplicitVersion(IPackage package, InstallOptions options) =>
            HasExplicitVersion(options) && HonorsVersionOnUpdate(package.Manager);

        public static string? HonoredUpdateVersion(IPackage package, InstallOptions options) =>
            options.Version.Length > 0
            && (
                package.Manager.Capabilities.SupportsCustomVersionsOnUpdate
                || (options.SkipMajorUpdates && HasHonoredExplicitVersion(package, options))
            )
                ? options.Version
                : null;

        public static bool Applies(IPackage package, InstallOptions options, OperationType operation) =>
            operation is OperationType.Update
            && options.SkipMajorUpdates
            && package.IsUpgradable
            && package.OverridenOptions.PinUpdateToNewVersion
            && package.NewVersionString.Length > 0
            && !HasExplicitVersion(options)
            && !package.IsUpdateMajor()
            && package.Manager.CanUpdateToExactVersion;

        public static TPackage KeepRecordedTarget<TPackage>(TPackage rebuiltUpdate, InstallOptions recordedOptions)
            where TPackage : IPackage
        {
            if (recordedOptions.SkipMajorUpdates)
                rebuiltUpdate.OverridenOptions.PinUpdateToNewVersion = true;
            return rebuiltUpdate;
        }

        public static InstallOptions Apply(IPackage package, InstallOptions options, OperationType operation)
        {
            if (!Applies(package, options, operation))
                return options;

            var pinned = options.Copy();
            pinned.Version = package.NewVersionString;
            pinned.VersionIsExplicit = true;
            return pinned;
        }
    }
}
