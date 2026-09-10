using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.PackageClasses
{
    public static class InstalledVersionNotice
    {
        public static string? BuildTooltip(IPackage package) =>
            BuildTooltip(
                package.InstalledVersionIsUnverified,
                package.VersionString,
                package.Manager.DisplayName
            );

        public static string? BuildTooltip(
            bool installedVersionIsUnverified,
            string versionString,
            string managerDisplayName
        )
        {
            if (!installedVersionIsUnverified)
                return null;

            string reason = CoreTools.Translate(
                "{0} could not read the version of this package that is currently installed.",
                managerDisplayName
            );

            string detail = versionString is null or "" or "Unknown"
                ? CoreTools.Translate("No installed version is known for it.")
                : CoreTools.Translate(
                    "The version shown is the one UniGetUI last installed ({0}); if the package has been updated by anything else since, that is out of date.",
                    versionString
                );

            return reason + "\n" + detail;
        }
    }
}
