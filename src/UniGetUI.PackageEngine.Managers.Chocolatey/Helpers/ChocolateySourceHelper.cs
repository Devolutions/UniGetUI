using System.Diagnostics;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.Providers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.PackageEngine.Managers.ChocolateyManager
{
    internal sealed class ChocolateySourceHelper : BaseSourceHelper
    {
        public ChocolateySourceHelper(Chocolatey manager)
            : base(manager) { }

        public override string[] GetAddSourceParameters(IManagerSource source)
        {
            return
            [
                "source",
                "add",
                "--name",
                source.Name,
                "--source",
                source.Url.ToString(),
                "-y",
            ];
        }

        public override string[] GetRemoveSourceParameters(IManagerSource source)
        {
            return ["source", "remove", "--name", source.Name, "-y"];
        }

        protected override OperationVeredict _getAddSourceOperationVeredict(
            IManagerSource source,
            int ReturnCode,
            string[] Output
        )
        {
            return ReturnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;
        }

        protected override OperationVeredict _getRemoveSourceOperationVeredict(
            IManagerSource source,
            int ReturnCode,
            string[] Output
        )
        {
            return ReturnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;
        }

        protected override IReadOnlyList<IManagerSource> GetSources_UnSafe()
        {
            using Process p = new()
            {
                StartInfo = new()
                {
                    FileName = Manager.Status.ExecutablePath,
                    Arguments =
                        Manager.Status.ExecutableCallArgs
                        + " source list "
                        + Chocolatey.GetProxyArgument(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                },
            };

            IProcessTaskLogger logger = Manager.TaskLogger.CreateNew(
                LoggableTaskType.ListSources,
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

            return ParseSources(lines);
        }

        internal IReadOnlyList<IManagerSource> ParseSources(IEnumerable<string> lines)
        {
            List<ManagerSource> sources = [];

            foreach (string line in lines)
            {
                try
                {
                    if (string.IsNullOrEmpty(line))
                    {
                        continue;
                    }

                    if (line.Contains(" - ") && line.Contains("| "))
                    {
                        string[] parts = line.Trim().Split('|')[0].Trim().Split(" - ");
                        if (
                            parts[1].Trim() == "https://community.chocolatey.org/api/v2/"
                            || parts[1].Trim() == "https://chocolatey.org/api/v2/"
                        )
                        {
                            sources.Add(
                                new ManagerSource(
                                    Manager,
                                    "community",
                                    new Uri("https://community.chocolatey.org/api/v2/")
                                )
                            );
                        }
                        else
                        {
                            sources.Add(
                                new ManagerSource(
                                    Manager,
                                    parts[0].Trim(),
                                    new Uri(parts[1].Split(" ")[0].Trim())
                                )
                            );
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }

            return sources;
        }
    }
}
