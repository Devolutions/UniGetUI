using System.Text.RegularExpressions;
using UniGetUI.Core.Data;
using UniGetUI.Core.IconEngine;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.Generic.NuGet.Internal;

namespace UniGetUI.PackageEngine.Managers.PowerShellManager
{
    public abstract class BaseNuGetDetailsHelper : BasePkgDetailsHelper
    {
        public BaseNuGetDetailsHelper(BaseNuGet manager)
            : base(manager) { }

        protected override void GetDetails_UnSafe(IPackageDetails details)
        {
            var logger = Manager.TaskLogger.CreateNew(LoggableTaskType.LoadPackageDetails);
            try
            {
                details.ManifestUrl = NuGetManifestLoader.GetManifestUrl(details.Package);
                string? PackageManifestContents = NuGetManifestLoader.GetManifestContent(
                    details.Package
                );
                logger.Log(PackageManifestContents);

                if (PackageManifestContents is null)
                {
                    logger.Error(
                        $"No manifest content could be loaded for package {details.Package.Id} on manager {details.Package.Manager.Name}, returning empty PackageDetails"
                    );
                    logger.Close(1);
                    return;
                }

                details.InstallerType = CoreTools.Translate("NuPkg (zipped manifest)");

                foreach (
                    Match match in Regex.Matches(
                        PackageManifestContents,
                        @"<content type=[""']\w+\/\w+[""'] src=""([^""]+)"" ?\/>"
                    )
                )
                {
                    try
                    {
                        details.InstallerUrl = new Uri(match.Groups[1].Value);
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(
                            $"Failed to parse NuGet Installer URL on package Id={details.Package.Id} for value={match.Groups[1].Value}: "
                                + ex.Message
                        );
                    }
                }

                foreach (
                    Match match in Regex.Matches(
                        PackageManifestContents,
                        @"<(d\:)?PackageSize (m\:type=""[^""]+"")?>([0-9]+)<\/"
                    )
                )
                {
                    try
                    {
                        details.InstallerSize = long.Parse(match.Groups[3].Value);
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(
                            $"Failed to parse NuGet Installer Size on package Id={details.Package.Id} for value={match.Groups[1].Value}: "
                                + ex.Message
                        );
                    }
                }

                foreach (
                    Match match in Regex.Matches(PackageManifestContents, @"<name>[^<>]+<\/name>")
                )
                {
                    details.Author = match.Value.Replace("<name>", "").Replace("</name>", "");
                    details.Publisher = match.Value.Replace("<name>", "").Replace("</name>", "");
                    break;
                }

                foreach (
                    Match match in Regex.Matches(
                        PackageManifestContents,
                        @"<d:Description>[^<>]+<\/d:Description>"
                    )
                )
                {
                    details.Description = match
                        .Value.Replace("<d:Description>", "")
                        .Replace("</d:Description>", "");
                    break;
                }

                foreach (
                    Match match in Regex.Matches(
                        PackageManifestContents,
                        @"<updated>[^<>]+<\/updated>"
                    )
                )
                {
                    details.UpdateDate = match
                        .Value.Replace("<updated>", "")
                        .Replace("</updated>", "");
                    break;
                }

                foreach (
                    Match match in Regex.Matches(
                        PackageManifestContents,
                        @"<d:ProjectUrl>[^<>]+<\/d:ProjectUrl>"
                    )
                )
                {
                    details.HomepageUrl = new Uri(
                        match.Value.Replace("<d:ProjectUrl>", "").Replace("</d:ProjectUrl>", "")
                    );
                    break;
                }

                foreach (
                    Match match in Regex.Matches(
                        PackageManifestContents,
                        @"<d:LicenseUrl>[^<>]+<\/d:LicenseUrl>"
                    )
                )
                {
                    details.LicenseUrl = new Uri(
                        match.Value.Replace("<d:LicenseUrl>", "").Replace("</d:LicenseUrl>", "")
                    );
                    break;
                }

                foreach (
                    Match match in Regex.Matches(
                        PackageManifestContents,
                        @"<d:PackageHash>[^<>]+<\/d:PackageHash>"
                    )
                )
                {
                    details.InstallerHash = match
                        .Value.Replace("<d:PackageHash>", "")
                        .Replace("</d:PackageHash>", "");
                    break;
                }

                foreach (
                    Match match in Regex.Matches(
                        PackageManifestContents,
                        @"<d:ReleaseNotes>[^<>]+<\/d:ReleaseNotes>"
                    )
                )
                {
                    details.ReleaseNotes = match
                        .Value.Replace("<d:ReleaseNotes>", "")
                        .Replace("</d:ReleaseNotes>", "");
                    break;
                }

                foreach (
                    Match match in Regex.Matches(
                        PackageManifestContents,
                        @"<d:LicenseNames>[^<>]+<\/d:LicenseNames>"
                    )
                )
                {
                    details.License = match
                        .Value.Replace("<d:LicenseNames>", "")
                        .Replace("</d:LicenseNames>", "");
                    break;
                }

                details.Dependencies.Clear();
                foreach (
                    Match match in Regex.Matches(
                        PackageManifestContents,
                        @"<d\:Dependencies>([^<]+)</d\:Dependencies>"
                    )
                )
                {
                    foreach (var dep in match.Groups[1].ToString().Split('|'))
                    {
                        if (string.IsNullOrEmpty(dep))
                            continue;
                        else if (dep.StartsWith("::"))
                            details.Dependencies.Add(
                                new()
                                {
                                    Name = dep.TrimStart(':'),
                                    Version = "",
                                    Mandatory = true,
                                }
                            );
                        else
                            details.Dependencies.Add(
                                new()
                                {
                                    Name = dep.Split(':')[0],
                                    Version = dep.Split(':')[1].TrimEnd(':'),
                                    Mandatory = true,
                                }
                            );
                    }
                }

                logger.Close(0);
                return;
            }
            catch (Exception e)
            {
                logger.Error(e);
                logger.Close(1);
                return;
            }
        }

        protected override CacheableIcon? GetIcon_UnSafe(IPackage package)
        {
            string? ManifestContent = NuGetManifestLoader.GetManifestContent(package);
            if (ManifestContent is null)
            {
                Logger.Warn(
                    $"No manifest content could be loaded for package {package.Id} on manager {package.Manager.Name}"
                );
                return null;
            }

            Match possibleIconUrl = Regex.Match(
                ManifestContent,
                "<(?:d\\:)?IconUrl>(.*)<(?:\\/d:)?IconUrl>"
            );

            if (!possibleIconUrl.Success || possibleIconUrl.Groups[1].Value == "")
            {
                // Logger.Warn($"No Icon URL could be parsed on the manifest Url={NuGetManifestLoader.GetManifestUrl(package).ToString()}");
                return null;
            }

            // Logger.Debug($"A native icon with Url={possibleIconUrl.Groups[1].Value} was found");
            return new CacheableIcon(
                new Uri(possibleIconUrl.Groups[1].Value),
                package.VersionString
            );
        }

        protected override IReadOnlyList<Uri> GetScreenshots_UnSafe(IPackage package)
        {
            throw new NotImplementedException();
        }

        protected override IReadOnlyList<string> GetInstallableVersions_UnSafe(IPackage package)
        {
            Uri SearchUrl = new($"{package.Source.Url}/FindPackagesById()?id='{package.Id}'");
            Logger.Debug(
                $"Begin package version search with url={SearchUrl} on manager {Manager.Name}"
            );

            List<string> results = [];

            using HttpClient client = new(CoreTools.GenericHttpClientParameters);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
            using var request = new HttpRequestMessage(HttpMethod.Get, SearchUrl);
            using HttpResponseMessage response = client.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn(
                    $"Failed to fetch api at Url={SearchUrl} with status code {response.StatusCode} to load versions"
                );
                return [];
            }

            string SearchResults = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            HashSet<string> alreadyProcessed = [];

            MatchCollection matches = Regex.Matches(SearchResults, "Version='([^<>']+)'");
            foreach (Match match in matches)
            {
                if (!alreadyProcessed.Contains(match.Groups[1].Value) && match.Success)
                {
                    results.Add(match.Groups[1].Value);
                    alreadyProcessed.Add(match.Groups[1].Value);
                }
            }

            results.Sort(StringComparer.OrdinalIgnoreCase);
            return results;
        }
    }
}
