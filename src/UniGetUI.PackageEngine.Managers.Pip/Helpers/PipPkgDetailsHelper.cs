using System.Diagnostics;
using UniGetUI.Core.IconEngine;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.Managers.Generic.PyPI;

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

            string json = PyPIProjectClient.FetchJson(details.Package.Id);

            if (PyPIProjectClient.ParseMetadata(json) is { } metadata)
                PyPIProjectClient.ApplyMetadata(details, metadata);

            if (PyPIProjectClient.ParseInstaller(json) is { } installer)
                PyPIProjectClient.ApplyInstaller(details, installer);

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
            return Pip.ResolveInstallLocation(
                Manager.Status.Version,
                Manager.Status.ExecutablePath,
                package.Id,
                Directory.Exists
            );
        }

        protected override IReadOnlyList<string> GetInstallableVersions_UnSafe(IPackage package) =>
            ListVersions(package, includePreReleases: false);

        internal IReadOnlyList<string> GetVersionsIncludingPreReleases(IPackage package)
        {
            RequireShellSafeId(package);
            return ListVersions(package, includePreReleases: true);
        }

        private IReadOnlyList<string> ListVersions(IPackage package, bool includePreReleases)
        {
            using Process p = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Manager.Status.ExecutablePath,
                    Arguments =
                        Manager.Status.ExecutableCallArgs
                        + " index versions "
                        + (includePreReleases ? "--pre " : "")
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
