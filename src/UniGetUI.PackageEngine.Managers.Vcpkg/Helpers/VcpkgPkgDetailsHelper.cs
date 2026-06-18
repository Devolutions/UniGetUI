using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UniGetUI.Core.Data;
using UniGetUI.Core.IconEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.PackageEngine.Managers.VcpkgManager
{
    internal sealed class VcpkgPkgDetailsHelper : BasePkgDetailsHelper
    {
        public VcpkgPkgDetailsHelper(Vcpkg manager)
            : base(manager) { }

        protected override void GetDetails_UnSafe(IPackageDetails details)
        {
            INativeTaskLogger logger = Manager.TaskLogger.CreateNew(
                LoggableTaskType.LoadPackageDetails
            );

            const string VCPKG_REPO = "microsoft/vcpkg";
            const string VCPKG_PORT_PATH = "master/ports";
            const string VCPKG_PORT_FILE = "vcpkg.json";
            string PackagePrefix = details.Package.Id.Split(":")[0];
            string PackageName = PackagePrefix.Split("[")[0];

            string JsonString;
            HttpClient client = new(CoreTools.GenericHttpClientParameters);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
            JsonString = client
                .GetStringAsync(
                    $"https://raw.githubusercontent.com/{VCPKG_REPO}/refs/heads/{VCPKG_PORT_PATH}/{PackageName}/{VCPKG_PORT_FILE}"
                )
                .GetAwaiter()
                .GetResult();

            JsonObject? contents = JsonNode.Parse(JsonString) as JsonObject;

            details.Description = contents?["description"]?.ToString();
            details.Publisher = contents?["maintainers"]?.ToString();
            // vcpkg doesn't store the author, for some reason???
            if (
                Uri.TryCreate(
                    contents?["homepage"]?.ToString(),
                    UriKind.RelativeOrAbsolute,
                    out var homepageUrl
                )
            )
                details.HomepageUrl = homepageUrl;
            details.License = contents?["license"]?.ToString();
            details.ManifestUrl = new Uri(
                $"https://github.com/{VCPKG_REPO}/blob/{VCPKG_PORT_PATH}/{PackageName}/{VCPKG_PORT_FILE}"
            );
            // TODO: since each change results in a new commit to the file, you could determine the `UpdateDate` via figuring out the date of the last commit that changed the file was.
            // Unfortunately, the GitHub API doesn't seem to allow getting the commit that changed a file, but you can get the date of a commit with
            // https://api.github.com/repos/{VCPKG_REPO}/commits/{CommitHash}

            List<string> Tags = [];
            // TODO: the "features" and "dependencies" keys could also be good candidates for tags, however their type specifications are all over -
            // strings, dictionaries, arrays - so one would first have to figure out how to handle that.
            // See https://learn.microsoft.com/en-us/vcpkg/reference/vcpkg-json
            if (PackagePrefix.Contains('['))
            {
                Tags.Add($"{CoreTools.Translate("library")}: " + PackageName);
                Tags.Add(
                    $"{CoreTools.Translate("feature")}: " + PackagePrefix.Split('[')[^1][..^1]
                );
            }

            details.Tags = Tags.ToArray();

            details.Dependencies.Clear();
            foreach (var iDep in contents?["dependencies"]?.AsArray() ?? [])
            {
                string? val = iDep?.GetValue<string>();
                if (val is not null)
                {
                    details.Dependencies.Add(
                        new()
                        {
                            Name = val,
                            Version = "",
                            Mandatory = true,
                        }
                    );
                }
            }

            logger.Close(0);
        }

        protected override CacheableIcon? GetIcon_UnSafe(IPackage package)
        {
            throw new NotImplementedException();
        }

        protected override IReadOnlyList<Uri> GetScreenshots_UnSafe(IPackage package)
        {
            throw new NotImplementedException();
        }

        protected override string? GetInstallLocation_UnSafe(IPackage package)
        {
            var (rootFound, rootPath) = Vcpkg.GetVcpkgRoot();
            if (!rootFound)
            {
                return null;
            }

            string PackageId = Regex.Replace(package.Id.Replace(":", "_"), @"\[.*\]", String.Empty);
            var PackagePath = Path.Join(rootPath, "packages", PackageId);
            var VcpkgInstalledPath = Path.Join(rootPath, "installed", package.Id.Split(":")[1]);
            return Directory.Exists(PackagePath)
                ? PackagePath
                : Directory.Exists(VcpkgInstalledPath)
                    ? VcpkgInstalledPath
                    : null;
        }

        protected override IReadOnlyList<string> GetInstallableVersions_UnSafe(IPackage package)
        {
            throw new NotImplementedException();
        }
    }
}
