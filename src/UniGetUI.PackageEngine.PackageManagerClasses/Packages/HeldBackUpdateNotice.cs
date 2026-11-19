using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.PackageClasses
{
    public static class HeldBackUpdateNotice
    {
        public static string? Build(IPackage package)
        {
            if (package.HeldBackUpdate is not { } held)
                return null;

            string reason = held.Reason switch
            {
                HeldBackUpdateReason.RefusedByPublisher => CoreTools.Translate(
                    "The publisher of this package requires updating it another way, for example from within the app itself, so {0} cannot install version {1}.",
                    package.Manager.DisplayName,
                    package.NewVersionString
                ),
                HeldBackUpdateReason.NotApplicable => CoreTools.Translate(
                    "{0} found no installer of version {1} that applies to this computer.",
                    package.Manager.DisplayName,
                    package.NewVersionString
                ),
                _ => CoreTools.Translate(
                    "{0} reported version {1} as installed {2} times, but version {3} is still installed.",
                    package.Manager.DisplayName,
                    package.NewVersionString,
                    held.Attempts,
                    package.VersionString
                ),
            };

            return reason
                + "\n"
                + CoreTools.Translate(
                    "Automatic updates and Update all leave this update out until a newer version is available."
                );
        }
    }
}
