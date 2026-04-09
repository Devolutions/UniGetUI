using System.Diagnostics;
using System.Text.RegularExpressions;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.Providers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.PackageEngine.Managers.ScoopManager
{
    internal sealed class ScoopSourceHelper : BaseSourceHelper
    {
        public ScoopSourceHelper(Scoop manager)
            : base(manager) { }

        protected override OperationVeredict _getAddSourceOperationVeredict(
            IManagerSource source,
            int ReturnCode,
            string[] Output
        )
        {
            return ReturnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;
        }

        public override string[] GetAddSourceParameters(IManagerSource source)
        {
            return ["bucket", "add", source.Name, source.Url.ToString()];
        }

        protected override OperationVeredict _getRemoveSourceOperationVeredict(
            IManagerSource source,
            int ReturnCode,
            string[] Output
        )
        {
            return ReturnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;
        }

        public override string[] GetRemoveSourceParameters(IManagerSource source)
        {
            return ["bucket", "rm", source.Name];
        }

        protected override IReadOnlyList<IManagerSource> GetSources_UnSafe()
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Manager.Status.ExecutablePath,
                    Arguments = Manager.Status.ExecutableCallArgs + " bucket list",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardInputEncoding = System.Text.Encoding.UTF8,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                },
            };

            IProcessTaskLogger logger = Manager.TaskLogger.CreateNew(
                LoggableTaskType.ListSources,
                p
            );

            p.Start();

            List<string> lines = [];
            string? line;
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
            bool dashesPassed = false;

            foreach (string line in lines)
            {
                try
                {
                    if (!dashesPassed)
                    {
                        if (line.Contains("---"))
                        {
                            dashesPassed = true;
                        }

                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    string[] elements = Regex
                        .Replace(
                            Regex.Replace(line, "[1234567890 :.-][AaPp][Mm][\\W]", "").Trim(),
                            " {2,}",
                            " "
                        )
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (elements.Length < 5)
                    {
                        continue;
                    }

                    if (
                        !elements[1].Contains("https://")
                        && !elements[1].Contains("http://")
                    )
                    {
                        elements[1] = Path.Join(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            "scoop",
                            "buckets",
                            elements[0].Trim()
                        );
                    }
                    else
                    {
                        elements[1] = Regex.Replace(elements[1], @"^(.*)\.git$", "$1");
                    }

                    try
                    {
                        sources.Add(
                            new ManagerSource(
                                Manager,
                                elements[0].Trim(),
                                new Uri(elements[1]),
                                int.Parse(elements[4].Trim()),
                                elements[2].Trim() + " " + elements[3].Trim()
                            )
                        );
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex);
                        sources.Add(
                            new ManagerSource(
                                Manager,
                                elements[0].Trim(),
                                new Uri(elements[1]),
                                -1,
                                "1/1/1970"
                            )
                        );
                    }
                }
                catch (Exception e)
                {
                    Logger.Warn(e);
                }
            }

            return sources;
        }
    }
}
