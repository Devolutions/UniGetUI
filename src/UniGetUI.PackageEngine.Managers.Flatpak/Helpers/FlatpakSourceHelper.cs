using System.Diagnostics;
using System.Text.RegularExpressions;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.Providers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.PackageEngine.Managers.FlatpakManager;

public partial class Flatpak
{
    [GeneratedRegex(@"^(\S+)\s+(https?://\S+)$")]
    internal static partial Regex RemoteListLineRegex();
}

internal sealed class FlatpakSourceHelper : BaseSourceHelper
{
    public FlatpakSourceHelper(Flatpak manager)
        : base(manager) { }

    protected override IReadOnlyList<IManagerSource> GetSources_UnSafe()
    {
        var sources = new List<ManagerSource>();

        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Manager.Status.ExecutablePath,
                Arguments = "remote-list --columns=name,url",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        IProcessTaskLogger logger = Manager.TaskLogger.CreateNew(LoggableTaskType.ListSources, p);
        p.Start();

        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            var match = Flatpak.RemoteListLineRegex().Match(line.Trim());
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups[1].Value;
            var url = match.Groups[2].Value;

            try
            {
                sources.Add(new ManagerSource(Manager, name, new Uri(url)));
            }
            catch (Exception ex)
            {
                Logger.Warn($"FlatpakSourceHelper: could not add remote '{name}': {ex.Message}");
            }
        }

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);
        return sources;
    }

    public override string[] GetAddSourceParameters(IManagerSource source)
        => ["remote-add", "--if-not-exists", source.Name, source.Url.ToString()];

    public override string[] GetRemoveSourceParameters(IManagerSource source)
        => ["remote-delete", source.Name];

    protected override OperationVeredict _getAddSourceOperationVeredict(
        IManagerSource source, int ReturnCode, string[] Output)
        => ReturnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;

    protected override OperationVeredict _getRemoveSourceOperationVeredict(
        IManagerSource source, int ReturnCode, string[] Output)
        => ReturnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;
}
