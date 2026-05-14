using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using UniGetUI.Core.IconEngine;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.PackageEngine.Managers.BunManager
{
    internal sealed class BunPkgDetailsHelper : BasePkgDetailsHelper
    {
        public BunPkgDetailsHelper(Bun manager) : base(manager) { }

        protected override void GetDetails_UnSafe(IPackageDetails details)
        {
            try
            {
                details.InstallerType = "Tarball";
                details.ManifestUrl = new Uri($"https://www.npmjs.com/package/{details.Package.Id}");
                details.ReleaseNotesUrl = new Uri($"https://www.npmjs.com/package/{details.Package.Id}?activeTab=versions");

                using Process p = new();
                p.StartInfo = new ProcessStartInfo
                {
                    FileName = Manager.Status.ExecutablePath,
                    Arguments = Manager.Status.ExecutableCallArgs + " info " + details.Package.Id + " --json --global",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };

                IProcessTaskLogger logger = Manager.TaskLogger.CreateNew(LoggableTaskType.LoadPackageDetails, p);
                p.Start();

                var stdoutTask = p.StandardOutput.ReadToEndAsync();
                var stderrTask = p.StandardError.ReadToEndAsync();
                Task.WaitAll(stdoutTask, stderrTask);

                string strContents = stdoutTask.Result;
                logger.AddToStdOut(strContents);
                JsonObject? contents = JsonNode.Parse(strContents) as JsonObject;

                details.License = contents?["license"]?.ToString();
                details.Description = contents?["description"]?.ToString();

                if (Uri.TryCreate(contents?["homepage"]?.ToString() ?? "", UriKind.RelativeOrAbsolute, out var homepageUrl))
                    details.HomepageUrl = homepageUrl;

                details.Publisher = (contents?["maintainers"] as JsonArray)?[0]?.ToString();
                // Handle author which can be string or object with "name" property
                var authorNode = contents?["author"];
                details.Author = authorNode is JsonObject authorObj ? authorObj["name"]?.ToString() : authorNode?.ToString();
                details.UpdateDate = contents?["time"]?[contents?["dist-tags"]?["latest"]?.ToString() ?? details.Package.VersionString]?.ToString();

                if (Uri.TryCreate(contents?["dist"]?["tarball"]?.ToString() ?? "", UriKind.RelativeOrAbsolute, out var installerUrl))
                    details.InstallerUrl = installerUrl;

                if (int.TryParse(contents?["dist"]?["unpackedSize"]?.ToString() ?? "", NumberStyles.Any, CultureInfo.InvariantCulture, out int installerSize))
                    details.InstallerSize = installerSize;

                details.InstallerHash = contents?["dist"]?["integrity"]?.ToString();

                details.Dependencies.Clear();
                HashSet<string> addedDeps = new();
                foreach (var rawDep in (contents?["dependencies"]?.AsObject() ?? []))
                {
                    if (addedDeps.Contains(rawDep.Key)) continue;
                    addedDeps.Add(rawDep.Key);

                    details.Dependencies.Add(new()
                    {
                        Name = rawDep.Key,
                        Version = rawDep.Value?.GetValue<string>() ?? "",
                        Mandatory = true,
                    });
                }

                foreach (var rawDep in (contents?["devDependencies"]?.AsObject() ?? []))
                {
                    if (addedDeps.Contains(rawDep.Key)) continue;
                    addedDeps.Add(rawDep.Key);

                    details.Dependencies.Add(new()
                    {
                        Name = rawDep.Key,
                        Version = rawDep.Value?.GetValue<string>() ?? "",
                        Mandatory = false,
                    });
                }

                foreach (var rawDep in (contents?["peerDependencies"]?.AsObject() ?? []))
                {
                    if (addedDeps.Contains(rawDep.Key)) continue;
                    addedDeps.Add(rawDep.Key);

                    details.Dependencies.Add(new()
                    {
                        Name = rawDep.Key,
                        Version = rawDep.Value?.GetValue<string>() ?? "",
                        Mandatory = false,
                    });
                }

                logger.AddToStdErr(stderrTask.Result);
                p.WaitForExit();
                logger.Close(p.ExitCode);
            }
            catch (Exception e)
            {
                Logger.Error(e);
            }

            return;
        }

        protected override CacheableIcon? GetIcon_UnSafe(IPackage package)
        {
            return null;
        }

        protected override IReadOnlyList<Uri> GetScreenshots_UnSafe(IPackage package)
        {
            return [];
        }

        protected override string? GetInstallLocation_UnSafe(IPackage package)
        {
            return GetInstallLocation(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                package.OverridenOptions.Scope,
                package.Id);
        }

        internal static string GetInstallLocation(string userProfile, string? scope, string packageId)
        {
            if (scope is PackageScope.Local)
                return Path.Join(userProfile, "node_modules", packageId);

            return Path.Join(Bun.GetGlobalPackagesDirectory(userProfile), "node_modules", packageId);
        }

        protected override IReadOnlyList<string> GetInstallableVersions_UnSafe(IPackage package)
        {
            using Process p = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Manager.Status.ExecutablePath,
                    Arguments =
                        Manager.Status.ExecutableCallArgs + " info " + package.Id + " --json --global",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                }
            };

            IProcessTaskLogger logger = Manager.TaskLogger.CreateNew(LoggableTaskType.LoadPackageVersions, p);
            p.Start();

            string strContents = p.StandardOutput.ReadToEnd();
            logger.AddToStdOut(strContents);
            JsonObject? contents = JsonNode.Parse(strContents) as JsonObject;
            JsonArray? rawVersions = contents?["versions"] as JsonArray;

            List<string> versions = [];
            foreach (JsonNode? raw_ver in rawVersions ?? [])
            {
                if (raw_ver is not null)
                    versions.Add(raw_ver.ToString());
            }

            logger.AddToStdErr(p.StandardError.ReadToEnd());
            p.WaitForExit();
            logger.Close(p.ExitCode);

            return versions;
        }
    }
}
