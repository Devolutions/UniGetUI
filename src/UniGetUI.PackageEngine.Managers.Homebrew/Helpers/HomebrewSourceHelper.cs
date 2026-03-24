using System.Diagnostics;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.Providers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.PackageEngine.Managers.HomebrewManager;

internal sealed class HomebrewSourceHelper : BaseSourceHelper
{
    public HomebrewSourceHelper(Homebrew manager)
        : base(manager) { }

    // ── Source listing ─────────────────────────────────────────────────────

    protected override IReadOnlyList<IManagerSource> GetSources_UnSafe()
    {
        var sources = new List<ManagerSource>();

        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Manager.Status.ExecutablePath,
                Arguments = "tap",
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
            var name = line.Trim();
            if (name.Length == 0) continue;

            // Build a best-effort URL: "org/repo" → "https://github.com/org/homebrew-repo"
            Uri url;
            try
            {
                var parts = name.Split('/');
                var org = parts[0];
                var repo = parts.Length > 1 ? parts[1] : name;
                // Official taps follow the "homebrew-<repo>" convention on GitHub
                url = new Uri($"https://github.com/{org}/homebrew-{repo}");
            }
            catch
            {
                url = new Uri($"https://github.com/{name}");
            }

            try
            {
                sources.Add(new ManagerSource(Manager, name, url));
            }
            catch (Exception ex)
            {
                Logger.Warn($"HomebrewSourceHelper: could not add tap '{name}': {ex.Message}");
            }
        }

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);
        return sources;
    }

    // ── Add / remove ───────────────────────────────────────────────────────

    public override string[] GetAddSourceParameters(IManagerSource source)
        => ["tap", source.Name, source.Url.ToString()];

    public override string[] GetRemoveSourceParameters(IManagerSource source)
        => ["untap", source.Name];

    protected override OperationVeredict _getAddSourceOperationVeredict(
        IManagerSource source, int ReturnCode, string[] Output)
        => ReturnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;

    protected override OperationVeredict _getRemoveSourceOperationVeredict(
        IManagerSource source, int ReturnCode, string[] Output)
        => ReturnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;
}
