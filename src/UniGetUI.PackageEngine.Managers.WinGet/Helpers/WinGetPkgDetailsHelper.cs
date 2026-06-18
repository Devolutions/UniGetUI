using System.Text.RegularExpressions;
using UniGetUI.Core.IconEngine;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.WinGet.ClientHelpers;

namespace UniGetUI.PackageEngine.Managers.WingetManager
{
    internal sealed class WinGetPkgDetailsHelper : BasePkgDetailsHelper
    {
        public WinGetPkgDetailsHelper(WinGet manager)
            : base(manager) { }

        protected override IReadOnlyList<string> GetInstallableVersions_UnSafe(IPackage package)
        {
            return WinGetHelper.Instance.GetInstallableVersions_Unsafe(package);
        }

        protected override void GetDetails_UnSafe(IPackageDetails details)
        {
            WinGetHelper.Instance.GetPackageDetails_UnSafe(details);
        }

        protected override CacheableIcon? GetIcon_UnSafe(IPackage package)
        {
            if (package.Source is LocalWinGetSource localSource)
            {
                if (localSource.Type is LocalWinGetSource.Type_t.MicrosftStore)
                    return WinGetIconsHelper.GetAppxPackageIcon(package);
                else if (localSource.Type is LocalWinGetSource.Type_t.LocalPC)
                    return WinGetIconsHelper.GetARPPackageIcon(package);

                return null;
            }

            if (package.Source.Name == "msstore")
                return WinGetIconsHelper.GetMicrosoftStoreIcon(package);

            return WinGetIconsHelper.GetWinGetPackageIcon(package);
        }

        protected override string? GetInstallLocation_UnSafe(IPackage package)
        {
            // Packages from the local "Add/Remove programs" source encode their exact registry key
            // in the Id, so their install location can be read straight from that ARP entry. The
            // generic name-based ARP lookup is handled by the base helper as a fallback.
            if (package.Source is LocalWinGetSource { Type: LocalWinGetSource.Type_t.LocalPC })
            {
                var encodedLocation = ArpRegistryHelper.GetLocationFromEncodedId(package.Id);
                if (encodedLocation is not null)
                    return encodedLocation;
            }

            // MSIX/Store packages from the Microsoft Store source encode their full name in the Id.
            var msixFromId = WinGetIconsHelper.GetMsixLocationFromId(package);
            if (msixFromId is not null)
                return msixFromId;

            // Match against Add/Remove programs by name before the heavier lookups below.
            var arpByName = ArpRegistryHelper.ResolveByName(package.Name, package.Id);
            if (arpByName is not null)
                return arpByName;

            foreach (
                var base_path in new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Path.Join(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Programs"
                    ),
                    Path.Join(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Microsoft",
                        "WinGet",
                        "Packages"
                    ),
                    Path.Join(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                        "WinGet",
                        "Packages"
                    ),
                    Path.Join(
                        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                        "WinGet",
                        "Packages"
                    ),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                }
            )
            {
                var path_with_name = Path.Join(base_path, package.Name);
                if (Directory.Exists(path_with_name))
                    return path_with_name;

                var path_with_id = Path.Join(base_path, package.Id);
                if (Directory.Exists(path_with_id))
                    return path_with_id;

                var path_with_source = Path.Join(base_path, $"{package.Id}_{package.Source.Name}");
                if (Directory.Exists(path_with_source))
                    return path_with_source;
            }

            // Last resort: enumerate installed MSIX packages (has a cost, so only reached when
            // nothing cheaper matched — e.g. Store apps surfaced through the winget catalog).
            return WinGetIconsHelper.GetMsixLocationByName(package);
        }

        protected override IReadOnlyList<Uri> GetScreenshots_UnSafe(IPackage package)
        {
            if (package.Source.Name != "msstore")
            {
                return [];
            }

            string? ResponseContent = WinGetIconsHelper.GetMicrosoftStoreManifest(package);
            if (ResponseContent is null)
            {
                return [];
            }

            Match IconArray = Regex.Match(
                ResponseContent,
                "(?:\"|')Images(?:\"|'): ?\\[([^\\]]+)\\]"
            );
            if (!IconArray.Success)
            {
                Logger.Warn("Could not parse Images array from Microsoft Store response");
                return [];
            }

            List<Uri> FoundIcons = [];

            foreach (Match ImageEntry in Regex.Matches(IconArray.Groups[1].Value, "{([^}]+)}"))
            {
                if (!ImageEntry.Success)
                {
                    continue;
                }

                Match ImagePurpose = Regex.Match(
                    ImageEntry.Groups[1].Value,
                    "(?:\"|')ImagePurpose(?:\"|'): ?(?:\"|')([^'\"]+)(?:\"|')"
                );
                if (!ImagePurpose.Success || ImagePurpose.Groups[1].Value != "Screenshot")
                {
                    continue;
                }

                Match ImageUrl = Regex.Match(
                    ImageEntry.Groups[1].Value,
                    "(?:\"|')Uri(?:\"|'): ?(?:\"|')([^'\"]+)(?:\"|')"
                );
                if (!ImageUrl.Success)
                {
                    continue;
                }

                FoundIcons.Add(new Uri("https:" + ImageUrl.Groups[1].Value));
            }

            return FoundIcons;
        }
    }
}
