using System.Diagnostics;
using System.Text.Json.Nodes;
using UniGetUI.Core.Data;
using UniGetUI.Core.IconEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.PackageEngine.Managers.PipManager
{
    internal sealed class PipPkgDetailsHelper : BasePkgDetailsHelper
    {
        public PipPkgDetailsHelper(Pip manager)
            : base(manager) { }

        protected override void GetDetails_UnSafe(IPackageDetails details)
        {
            INativeTaskLogger logger = Manager.TaskLogger.CreateNew(
                LoggableTaskType.LoadPackageDetails
            );

            using HttpClient client = new(CoreTools.GenericHttpClientParameters);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://pypi.org/pypi/{details.Package.Id}/json"
            );
            using HttpResponseMessage response = client.Send(request);
            response.EnsureSuccessStatusCode();
            string JsonString = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            JsonObject? contents = JsonNode.Parse(JsonString) as JsonObject;

            if (contents?["info"] is JsonObject info)
            {
                details.Description = info["summary"]?.ToString();
                details.Author = info["author"]?.ToString();
                details.Publisher = info["maintainer"]?.ToString();
                details.License = info["license"]?.ToString();

                if (
                    Uri.TryCreate(
                        info["home_page"]?.ToString(),
                        UriKind.RelativeOrAbsolute,
                        out var homepageUrl
                    )
                )
                    details.HomepageUrl = homepageUrl;

                if (
                    Uri.TryCreate(
                        info["package_url"]?.ToString(),
                        UriKind.RelativeOrAbsolute,
                        out var packageUrl
                    )
                )
                    details.ManifestUrl = packageUrl;

                if (info["classifiers"] is JsonArray classifiers)
                {
                    List<string> Tags = [];
                    foreach (string? line in classifiers)
                    {
                        if (line?.Contains("License ::") ?? false)
                        {
                            details.License = line.Split("::")[^1].Trim();
                        }
                        else if (line?.Contains("Topic ::") ?? false)
                        {
                            if (!Tags.Contains(line.Split("::")[^1].Trim()))
                                Tags.Add(line.Split("::")[^1].Trim());
                        }
                    }
                    details.Tags = Tags.ToArray();
                }

                details.Dependencies.Clear();
                foreach (var rawDep in (info?["requires_dist"]?.AsArray() ?? []))
                {
                    string line = rawDep?.GetValue<string>().Split(';')[0] ?? "";
                    string name = line.Split(['>', '<', '=', '!'])[0];
                    details.Dependencies.Add(
                        new()
                        {
                            Name = name,
                            Version = line[name.Length..],
                            Mandatory = true,
                        }
                    );
                }
            }

            JsonObject? url = contents?["url"] as JsonObject;
            url ??= (contents?["urls"] as JsonArray)?[0] as JsonObject;

            if (url is not null)
            {
                if (url["digests"] is JsonObject digests)
                    details.InstallerHash = digests["sha256"]?.ToString();

                if (
                    Uri.TryCreate(
                        url["url"]?.ToString(),
                        UriKind.RelativeOrAbsolute,
                        out var installerUrl
                    )
                )
                {
                    details.InstallerType = url["url"]
                        ?.ToString()
                        .Split('.')[^1]
                        .Replace("whl", "Wheel");
                    details.InstallerUrl = installerUrl;
                    details.InstallerSize = CoreTools.GetFileSizeAsLong(installerUrl);
                }

                details.UpdateDate = url["upload_time"]?.ToString();
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
            var full_path = Path.Join(
                Path.GetDirectoryName(Manager.Status.ExecutablePath),
                "Lib",
                "site-packages",
                package.Id
            );
            return Directory.Exists(full_path) ? full_path : Path.GetDirectoryName(full_path);
        }

        protected override IReadOnlyList<string> GetInstallableVersions_UnSafe(IPackage package)
        {
            using Process p = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Manager.Status.ExecutablePath,
                    Arguments =
                        Manager.Status.ExecutableCallArgs
                        + " index versions "
                        + package.Id
                        + " "
                        + Pip.GetProxyArgument(),
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                },
            };

            IProcessTaskLogger logger = Manager.TaskLogger.CreateNew(
                LoggableTaskType.LoadPackageVersions,
                p
            );
            p.Start();

            string? line;
            string[] result = [];
            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                logger.AddToStdOut(line);
                if (line.Contains("Available versions:"))
                {
                    result = line.Replace("Available versions:", "").Trim().Split(", ");
                }
            }

            logger.AddToStdErr(p.StandardError.ReadToEnd());
            p.WaitForExit();
            logger.Close(p.ExitCode);

            return result;
        }
    }
}
