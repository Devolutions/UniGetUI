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
    private readonly Homebrew _brew;

    public HomebrewSourceHelper(Homebrew manager)
        : base(manager) { _brew = manager; }

    // ── Source listing ─────────────────────────────────────────────────────

    protected override IReadOnlyList<IManagerSource> GetSources_UnSafe()
    {
        var tapLines = new List<string>();

        using var p = new Process
        {
            StartInfo = _brew.MakeBrewStartInfo("tap"),
        };
        IProcessTaskLogger logger = Manager.TaskLogger.CreateNew(LoggableTaskType.ListSources, p);
        p.Start();

        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            tapLines.Add(line);
        }

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);
        return BuildSourceList(tapLines);
    }

    /// <summary>
    /// Homebrew 4 and later serve homebrew/core and homebrew/cask from the API, so `brew tap` does not
    /// print them and `brew tap homebrew/core` is refused. The built-in sources are therefore always
    /// listed, followed by every other tap.
    /// </summary>
    internal IReadOnlyList<IManagerSource> BuildSourceList(IEnumerable<string> tapLines)
    {
        var sources = new List<IManagerSource>(Manager.Properties.KnownSources);

        foreach (string rawLine in tapLines)
        {
            var name = rawLine.Trim();
            if (name.Length == 0) continue;
            if (name.Equals(CoreTap, StringComparison.OrdinalIgnoreCase)
                || name.Equals(CaskTap, StringComparison.OrdinalIgnoreCase))
                continue;

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

        return sources;
    }

    // ── Add / remove ───────────────────────────────────────────────────────

    internal const string CoreTap = "homebrew/core";
    internal const string CaskTap = "homebrew/cask";

    /// <summary>
    /// The tap name brew expects for a source: the built-in "Homebrew" and "Homebrew Cask" sources map to
    /// homebrew/core and homebrew/cask; any other source is named after its tap already.
    /// </summary>
    internal static string GetTapName(IManagerSource source) => source.Name switch
    {
        "Homebrew" => CoreTap,
        "Homebrew Cask" => CaskTap,
        _ => source.Name,
    };

    public override string[] GetAddSourceParameters(IManagerSource source)
    {
        string tap = GetTapName(source);
        return tap == source.Name ? ["tap", tap, source.Url.ToString()] : ["tap", tap];
    }

    public override string[] GetRemoveSourceParameters(IManagerSource source)
        => ["untap", GetTapName(source)];

    protected override OperationVeredict _getAddSourceOperationVeredict(
        IManagerSource source, int ReturnCode, string[] Output)
        => ReturnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;

    protected override OperationVeredict _getRemoveSourceOperationVeredict(
        IManagerSource source, int ReturnCode, string[] Output)
        => ReturnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;
}
