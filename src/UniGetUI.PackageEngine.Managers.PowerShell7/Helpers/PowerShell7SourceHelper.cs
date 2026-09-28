using System.Diagnostics;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.Providers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.PackageEngine.Managers.PowerShell7Manager
{
    internal sealed class PowerShell7SourceHelper : BaseSourceHelper
    {
        public PowerShell7SourceHelper(PowerShell7 manager)
            : base(manager) { }

        public override string[] GetAddSourceParameters(IManagerSource source)
        {
            if (source.Url.ToString() == "https://www.powershellgallery.com/api/v2")
            {
                return ["Register-PSRepository", "-Default"];
            }

            return
            [
                "Register-PSRepository",
                "-Name",
                source.Name,
                "-SourceLocation",
                source.Url.ToString(),
            ];
        }

        public override string[] GetRemoveSourceParameters(IManagerSource source)
        {
            return ["Unregister-PSRepository", "-Name", source.Name];
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
                        + " \"if (Get-Command Get-PSResourceRepository -ErrorAction SilentlyContinue)"
                        + " { Get-PSResourceRepository | Format-Table -Property Name,Uri"
                        + ManagerTable.UntruncatedTableTail
                        + " } else { Get-PSRepository | Format-Table -Property"
                        + " Name,@{N='SourceLocation';E={If ($_.Uri) {$_.Uri.AbsoluteUri} Else {$_.SourceLocation}}}"
                        + ManagerTable.UntruncatedTableTail
                        + " }\"",
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
            List<IManagerSource> sources = [];
            IReadOnlyList<int>? columns = null;

            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (columns is null)
                {
                    columns = ManagerTable.ReadColumnStarts(line);
                    continue;
                }

                string name = ManagerTable.ReadColumn(line, columns, 0);
                string location = ManagerTable.ReadColumn(line, columns, 1);

                if (name.Length is 0 || location.Length is 0)
                {
                    continue;
                }

                if (!Uri.TryCreate(location, UriKind.Absolute, out Uri? url))
                {
                    Logger.Warn(
                        $"Could not read the location \"{location}\" of the "
                            + $"{Manager.Name} repository {name}"
                    );
                    continue;
                }

                sources.Add(new ManagerSource(Manager, name, url));
            }

            return sources;
        }
    }
}
