using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.PackageClasses
{
    public static class UpdateVersionPin
    {
        public static bool HasExplicitVersion(InstallOptions options) =>
            options.VersionIsExplicit && options.Version.Length > 0;

        public static bool Applies(IPackage package, InstallOptions options, OperationType operation) =>
            operation is OperationType.Update
            && options.SkipMajorUpdates
            && package.IsUpgradable
            && package.NewVersionString.Length > 0
            && !HasExplicitVersion(options)
            && !package.IsUpdateMajor()
            && package.Manager.CanUpdateToExactVersion;

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
