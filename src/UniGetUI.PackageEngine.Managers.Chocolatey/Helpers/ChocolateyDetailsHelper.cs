using System.Diagnostics;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.Managers.ChocolateyManager;
using UniGetUI.PackageEngine.Managers.PowerShellManager;

namespace UniGetUI.PackageEngine.Managers.Choco
{
    public class ChocolateyDetailsHelper : BaseNuGetDetailsHelper
    {
        public ChocolateyDetailsHelper(BaseNuGet manager)
            : base(manager) { }

        internal static IReadOnlyList<string> ParseInstallableVersions(IEnumerable<string> lines)
        {
            List<string> versions = [];
            foreach (string line in lines)
            {
                if (line.Contains("[Approved]"))
                {
                    versions.Add(line.Split(' ')[1].Trim());
                }
            }

            return versions;
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
                        + $" search {package.Id} --exact --all-versions "
                        + Chocolatey.GetProxyArgument(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
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
            List<string> lines = [];
            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                logger.AddToStdOut(line);
                lines.Add(line);
            }
            logger.AddToStdErr(p.StandardError.ReadToEnd());
            p.WaitForExit();
            logger.Close(p.ExitCode);

            return ParseInstallableVersions(lines);
        }

        protected override string? GetInstallLocation_UnSafe(IPackage package)
        {
            string portable_path = Manager.Status.ExecutablePath.Replace(
                "choco.exe",
                $"bin\\{package.Id}.exe"
            );
            if (File.Exists(portable_path))
                return Path.GetDirectoryName(portable_path);

            foreach (
                var base_path in new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Path.Join(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Programs"
                    ),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                }
            )
            {
                var path_with_id = Path.Join(base_path, package.Id);
                if (Directory.Exists(path_with_id))
                    return path_with_id;
            }

            return Path.Join(
                Path.GetDirectoryName(Manager.Status.ExecutablePath),
                "lib",
                package.Id
            );
        }
    }
}
