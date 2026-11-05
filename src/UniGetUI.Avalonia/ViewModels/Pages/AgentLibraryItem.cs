using UniGetUI.Core.Tools;
using UniGetUI.Extensions.AgentSkills;

namespace UniGetUI.Avalonia.ViewModels.Pages;

public enum AgentLibraryView { Discover, Installed, Updates, Sources }
public enum AgentLibraryTypeFilter { All, Skills, Plugins }
public enum AgentLibraryItemType { Skill, Plugin }

public sealed record AgentLibraryToolOption(string Name, string? AgentId, IAgentPluginService? PluginProvider);

public sealed record AgentLibraryItem(
    string Key,
    string Name,
    string Description,
    AgentLibraryItemType Type,
    string Source,
    string Status,
    string InstalledFor,
    bool IsInstalled,
    bool HasUpdate,
    bool CanInstall,
    bool CanUpdate,
    SkillItem? Skill,
    AgentPluginItem? Plugin,
    IAgentPluginService? PluginProvider)
{
    public string TypeLabel => CoreTools.Translate(Type == AgentLibraryItemType.Skill ? "Skill" : "Plugin");
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public bool HasPositiveStatus => IsInstalled && (Plugin is null || IsEnabled);
    public string SourceSummary => string.IsNullOrWhiteSpace(InstalledFor)
        || string.Equals(Source, InstalledFor, StringComparison.OrdinalIgnoreCase)
        ? Source : $"{Source} \u00b7 {InstalledFor}";
    public bool HasMarketplace => !string.IsNullOrWhiteSpace(Plugin?.Marketplace);
    public bool CanUninstall => Skill?.IsInstalled == true || Plugin is { IsInstalled: true, CanManage: true };
    public bool CanPluginUninstall => Plugin is { IsInstalled: true, CanManage: true };
    public bool CanEnable => Plugin is { IsInstalled: true, CanToggle: true, Enabled: false };
    public bool CanDisable => Plugin is { IsInstalled: true, CanToggle: true, Enabled: true };
    public bool IsEnabled => Plugin?.Enabled == true;
    public string Notice => Plugin?.Notice ?? "";
    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);
}
