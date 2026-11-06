using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;
using UniGetUI.Extensions.AgentSkills;
using UniGetUI.PackageEngine.Classes.Manager.Classes;
using UniGetUI.PackageEngine.Classes.Packages.Classes;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>UI dependency-install bridge; the extension service never registers a package manager.</summary>
internal static class AgentSkillsDependencies
{
    internal const string IgnoredUpdatesId = "winget\\Notion.ntn";
    private static readonly string[] IgnoredUpdatesIds = [IgnoredUpdatesId, "npm\\ntn"];
    public static bool TrySetPinnedVersion(string version)
    {
        bool wasPinned = NotionCliTool.PinnedVersion is not null;
        if (!NotionCliTool.TrySetPinnedVersion(version)) return false;
        if (NotionCliTool.PinnedVersion is not null) ApplyPinnedUpdateExclusions();
        else if (wasPinned)
            foreach (string id in IgnoredUpdatesIds)
                if (IgnoredUpdatesDatabase.GetIgnoredVersion(id) is not null)
                    IgnoredUpdatesDatabase.Remove(id);
        return true;
    }
    public static void ApplyPinnedUpdateExclusions()
    {
        if (NotionCliTool.PinnedVersion is null) return;
        foreach (string id in IgnoredUpdatesIds) IgnoredUpdatesDatabase.Add(id);
    }
    public static ManagerDependency? ForSource(SkillSourceLocator source) => source.Kind switch
    {
        SkillSourceKind.Repository => GitDependency.Create(),
        SkillSourceKind.Notion => CreateNotionDependency(),
        _ => null,
    };
    public static ManagerDependency CreateNotionDependency()
    {
        string? version = NotionCliTool.PinnedVersion;
        Func<Task<bool>> installed = () => Task.Run(() =>
        {
            NotionCliTool.Forget();
            return NotionCliTool.FindExecutable() is not null;
        });
        if (OperatingSystem.IsWindows())
        {
            string pin = version is null ? "" : $" --version {version}";
            return new(NotionCliTool.Name, CoreData.PowerShell5,
                "-ExecutionPolicy Bypass -NoLogo -NoProfile -Command \"& {winget install --id Notion.ntn --exact --source winget --accept-source-agreements --accept-package-agreements --force"
                + pin + "}\"", $"winget install --id Notion.ntn --exact --source winget{pin}", installed);
        }
        string package = version is null ? "ntn" : $"ntn@{version}";
        bool npm = CoreTools.Which("npm").Item1;
        return new(NotionCliTool.Name, npm ? "npm" : "", npm ? $"install --global {package}" : "",
            npm ? $"npm install --global {package}" : "curl -fsSL https://ntn.dev | bash", installed);
    }
}
