using UniGetUI.Core.Tools;

namespace UniGetUI.Extensions.AgentSkills;

public enum AgentPluginAction { Install, Update, Enable, Disable, Uninstall }

public sealed record AgentPluginItem(string Id, string Description, string Version, bool IsInstalled, bool Enabled)
{
    private bool? _canToggle;
    public string Name => Id.Split('@')[0];
    public string Marketplace => Id.Contains('@') ? Id[(Id.IndexOf('@') + 1)..] : "";
    public string Status => StatusText ?? (!IsInstalled ? CoreTools.Translate("Not installed")
        : Enabled ? CoreTools.Translate("Enabled") : CoreTools.Translate("Disabled"));
    public string? StatusText { get; init; }
    public bool CanInstall { get; init; } = true;
    public bool CanManage { get; init; } = true;
    /// <summary>Whether an installed plugin can be turned on or off. Defaults to <see cref="CanManage"/>.</summary>
    public bool CanToggle { get => _canToggle ?? CanManage; init => _canToggle = value; }
    public string? Notice { get; init; }
    public bool ShowInstall => !IsInstalled && CanInstall;
    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);
}

public sealed record AgentPluginMarketplace(string Name, string Source)
{
    private bool? _canRefresh;
    public bool CanManage { get; init; } = true;
    public bool CanRefresh { get => _canRefresh ?? CanManage; init => _canRefresh = value; }
}
public sealed record AgentPluginCatalog(IReadOnlyList<AgentPluginItem> Plugins, IReadOnlyList<AgentPluginMarketplace> Marketplaces);

public interface IAgentPluginService
{
    string Name { get; }
    string Guidance { get; }
    string RemovalWarning { get; }
    string RestartNotice { get; }
    bool SupportsUpdate { get; }
    bool SupportsToggle { get; }
    bool SupportsScopedBrowse => false;
    string CacheIdentity => Name;
    Task<AgentPluginCatalog> GetCatalogAsync(CancellationToken token = default);
    Task<AgentPluginCatalog> GetInstalledCatalogAsync(CancellationToken token = default) => GetCatalogAsync(token);
    async Task<AgentPluginCatalog> GetMarketplaceCatalogAsync(string marketplace, CancellationToken token = default)
    {
        var catalog = await GetCatalogAsync(token);
        token.ThrowIfCancellationRequested();
        return catalog with
        {
            Plugins = catalog.Plugins.Where(item => !item.IsInstalled && item.Marketplace == marketplace).ToArray(),
        };
    }
    Task<string> AddMarketplaceAsync(string input, CancellationToken token = default);
    Task<string> UpdateMarketplaceAsync(string name, CancellationToken token = default);
    Task<string> RemoveMarketplaceAsync(string name, CancellationToken token = default);
    Task<string> PerformAsync(string input, AgentPluginAction action, CancellationToken token = default);
}

internal static class AgentPluginInput
{
    public static string StripCommand(string input, params string[] prefixes)
    {
        string value = input.Trim();
        foreach (string prefix in prefixes)
            if (value.StartsWith(prefix, StringComparison.Ordinal)) return value[prefix.Length..].Trim();
        return value;
    }

    public static string Value(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-') || value.Any(char.IsControl))
            throw new ArgumentException(CoreTools.Translate("Enter a valid marketplace or plugin identifier."));
        return value;
    }

    public static string Segment(string value)
    {
        Value(value);
        if (value is "." or ".." || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
            throw new ArgumentException(CoreTools.Translate("Enter a valid marketplace or plugin identifier."));
        return value;
    }

    public static string PluginId(string value)
    {
        Value(value);
        string[] parts = value.Split('@');
        if (parts.Length != 2 || parts.Any(part => string.IsNullOrEmpty(part)))
            throw new ArgumentException(CoreTools.Translate("Enter a plugin identifier in name@marketplace format."));
        foreach (string part in parts) Segment(part);
        return value;
    }
}
