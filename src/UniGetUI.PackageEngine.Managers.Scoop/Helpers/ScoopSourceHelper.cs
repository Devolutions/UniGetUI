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
                    Arguments =
                        Manager.Status.ExecutableCallArgs
                        + " bucket list"
                        + Scoop.UntruncatedTableOutput,
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
            p.StandardInput.Close();

            return ParseSources(ScoopProcess.ReadLines(p, logger));
        }

        internal IReadOnlyList<IManagerSource> ParseSources(IEnumerable<string> lines)
        {
            List<ManagerSource> sources = [];
            IReadOnlyList<int>? columns = null;

            foreach (string rawLine in lines)
            {
                string line = ScoopTable.StripAnsiSequences(rawLine);

                if (columns is null)
                {
                    columns = ScoopTable.ReadColumnStarts(line);
                    continue;
                }

                if (columns.Count < 4 || string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                string name = ScoopTable.ReadColumn(line, columns, 0);
                string source = ScoopTable.ReadColumn(line, columns, 1);
                string updated = ScoopTable.ReadColumn(line, columns, 2);
                string manifests = ScoopTable.ReadColumn(line, columns, 3);

                if (name.Length is 0 || source.Length is 0 || manifests.Length is 0)
                {
                    continue;
                }

                try
                {
                    Uri url = BuildSourceUrl(name, source);

                    sources.Add(
                        int.TryParse(manifests, out int packageCount)
                            ? new ManagerSource(
                                Manager,
                                name,
                                url,
                                packageCount,
                                Regex.Replace(updated, @"\s+[AaPp][Mm]$", "")
                            )
                            : new ManagerSource(Manager, name, url, -1, "1/1/1970")
                    );
                }
                catch (Exception e)
                {
                    Logger.Warn(e);
                }
            }

            return sources;
        }

        private static Uri BuildSourceUrl(string name, string source)
        {
            if (source.Contains("https://") || source.Contains("http://"))
            {
                return new Uri(Regex.Replace(source, @"^(.*)\.git$", "$1"));
            }

            string userProfile = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile
            );
            string path =
                source.StartsWith("~/") || source.StartsWith(@"~\")
                    ? Path.Join(userProfile, source[2..])
                    : source;

            return Path.IsPathFullyQualified(path)
                ? new Uri(path)
                : new Uri(Path.Join(userProfile, "scoop", "buckets", name));
        }
    }
}
