using System.Text.RegularExpressions;

namespace UniGetUI.PackageEngine.ManagerClasses.Manager
{
    internal static partial class PreReleaseVersion
    {
        public static bool IsPreRelease(string version) => Marker().IsMatch(version);

        [GeneratedRegex(
            @"alpha|beta|preview|pre|rc|dev|snapshot|nightly|canary|insider|\d[ab]\d|^[vV]?\d+(\.\d+)*-",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
        )]
        private static partial Regex Marker();
    }
}
