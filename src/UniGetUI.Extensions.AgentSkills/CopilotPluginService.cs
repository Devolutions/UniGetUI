using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using UniGetUI.Core.Tools;

namespace UniGetUI.Extensions.AgentSkills;

public sealed class CopilotPluginService : IAgentPluginService
{
    private readonly IAgentPluginRunner _runner;
    public string Name => "GitHub Copilot";
    public string CacheIdentity => $"{Name}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable("COPILOT_HOME") ?? "")))}";
    public string Guidance => CoreTools.Translate("Manage the local Copilot plugin inventory shared with the desktop app. Repo settings and managed policy may change effective enablement. Add or install here, or open the app confirmation form.");
    public string RemovalWarning => CoreTools.Translate("Remove this marketplace from Copilot? Uninstall its plugins first. Built-in and policy-managed marketplaces cannot be removed here.");
    public string RestartNotice => CoreTools.Translate("Plugin changes use the local Copilot profile. Start a new Copilot session to apply them; active desktop sessions may need to reload.");
    public bool SupportsUpdate => true;
    public bool SupportsToggle => true;
    public bool SupportsScopedBrowse => true;

    public CopilotPluginService() : this(new AgentPluginProcessRunner(
        () => AgentPluginProcessRunner.FindNativeExecutable("copilot"), TimeSpan.FromMinutes(3), "GitHub Copilot CLI"))
    { }
    internal CopilotPluginService(IAgentPluginRunner runner) => _runner = runner;

    public async Task<AgentPluginCatalog> GetCatalogAsync(CancellationToken token = default)
    {
        AgentPluginCatalog installed = await GetInstalledCatalogAsync(token);
        Dictionary<string, AgentPluginItem> items = new(StringComparer.Ordinal);
        foreach (var marketplace in installed.Marketplaces)
        {
            AgentPluginCatalog browse = await GetMarketplaceCatalogAsync(marketplace.Name, token);
            foreach (var plugin in browse.Plugins)
                items[plugin.Id] = plugin;
        }
        foreach (var plugin in installed.Plugins)
            items[plugin.Id] = items.TryGetValue(plugin.Id, out var available) && string.IsNullOrEmpty(plugin.Description)
                ? plugin with { Description = available.Description }
                : plugin;
        return new(items.Values.OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToArray(), installed.Marketplaces);
    }

    public async Task<AgentPluginCatalog> GetInstalledCatalogAsync(CancellationToken token = default)
    {
        var installed = Deserialize(
            await _runner.RunAsync(["plugin", "list", "--json"], token), CopilotPluginJsonContext.Default.CopilotPluginEntryArray)
            ?? throw InvalidInventory();
        var markets = Deserialize(
            await _runner.RunAsync(["plugin", "marketplace", "list", "--json"], token),
            CopilotPluginJsonContext.Default.CopilotMarketplaceEntryArray) ?? throw InvalidInventory();
        var marketplaceItems = ParseMarketplaces(markets);
        var plugins = ParseInstalled(installed);
        return new(plugins, marketplaceItems);
    }

    public async Task<AgentPluginCatalog> GetMarketplaceCatalogAsync(string marketplace, CancellationToken token = default)
    {
        string name = AgentPluginInput.Segment(marketplace);
        var available = Deserialize(
            await _runner.RunAsync(["plugin", "marketplace", "browse", name, "--json"], token),
            CopilotPluginJsonContext.Default.CopilotPluginEntryArray) ?? throw InvalidInventory();
        HashSet<string> ids = new(StringComparer.Ordinal);
        var items = available.Select(plugin =>
        {
            if (plugin is null || plugin.Marketplace != name) throw InvalidInventory();
            string id = $"{AgentPluginInput.Segment(plugin.Name ?? "")}@{name}";
            if (!ids.Add(id)) throw InvalidInventory();
            return new AgentPluginItem(id, plugin.Description ?? "", plugin.Version ?? "", false, false);
        }).ToArray();
        return new(items, []);
    }

    private static AgentPluginItem[] ParseInstalled(CopilotPluginEntry[] installed)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        return installed.Select(plugin =>
        {
            if (plugin is null) throw InvalidInventory();
            string name = AgentPluginInput.Segment(plugin.Name ?? "");
            string id = string.IsNullOrEmpty(plugin.Marketplace) ? name
                : $"{name}@{AgentPluginInput.Segment(plugin.Marketplace)}";
            if (plugin.Enabled is null || string.IsNullOrEmpty(plugin.Source) || !ids.Add(id))
                throw InvalidInventory();
            bool manageable = plugin.Source == "installed" && !plugin.Managed;
            return new AgentPluginItem(id, plugin.Description ?? "", plugin.Version ?? "", true, plugin.Enabled.Value)
            {
                CanManage = manageable,
                Notice = manageable ? null : CoreTools.Translate("Built-in, external or policy-managed plugin. Manage this entry in Copilot."),
            };
        }).ToArray();
    }

    private static AgentPluginMarketplace[] ParseMarketplaces(CopilotMarketplaceEntry[] markets)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        return markets.Select(market =>
        {
            if (market is null) throw InvalidInventory();
            string name = AgentPluginInput.Segment(market.Name ?? "");
            if (string.IsNullOrWhiteSpace(market.Source) || !names.Add(name)) throw InvalidInventory();
            return new AgentPluginMarketplace(name, market.Source)
            {
                CanManage = !market.IsDefault && !market.Managed,
                CanRefresh = !market.Managed,
            };
        }).ToArray();
    }

    public Task<string> AddMarketplaceAsync(string input, CancellationToken token = default) =>
        _runner.RunAsync(["plugin", "marketplace", "add", MarketplaceSource(input)], token);
    public Task<string> UpdateMarketplaceAsync(string name, CancellationToken token = default) =>
        _runner.RunAsync(["plugin", "marketplace", "update", AgentPluginInput.Segment(name)], token);
    public Task<string> RemoveMarketplaceAsync(string name, CancellationToken token = default) =>
        _runner.RunAsync(["plugin", "marketplace", "remove", AgentPluginInput.Segment(name)], token);

    public Task<string> PerformAsync(string input, AgentPluginAction action, CancellationToken token = default)
    {
        string value = action == AgentPluginAction.Install ? InstallSource(input) : AgentPluginInput.Value(input.Trim());
        string command = action switch
        {
            AgentPluginAction.Install => "install",
            AgentPluginAction.Update => "update",
            AgentPluginAction.Enable => "enable",
            AgentPluginAction.Disable => "disable",
            AgentPluginAction.Uninstall => "uninstall",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        return _runner.RunAsync(["plugin", command, value], token);
    }

    internal static string MarketplaceSource(string input) => AgentPluginInput.Value(AgentPluginInput.StripCommand(input,
        "copilot plugin marketplace add ", "copilot plugins marketplace add ", "/plugin marketplace add "));
    internal static string InstallSource(string input) => AgentPluginInput.Value(AgentPluginInput.StripCommand(input,
        "copilot plugin install ", "copilot plugins install ", "copilot plugin add ", "/plugin install "));
    private static JsonException InvalidInventory() => new(CoreTools.Translate("Unsupported Copilot plugin inventory. Update Copilot CLI and reload the list."));

    private static T? Deserialize<T>(string json, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Deserialize(json, typeInfo);
}

internal sealed class CopilotPluginEntry
{
    public string? Name { get; set; }
    public string? Marketplace { get; set; }
    public string? Description { get; set; }
    public string? Version { get; set; }
    public bool? Enabled { get; set; }
    public string? Source { get; set; }
    public bool Managed { get; set; }
}

internal sealed class CopilotMarketplaceEntry
{
    public string? Name { get; set; }
    public string? Source { get; set; }
    public bool IsDefault { get; set; }
    public bool Managed { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CopilotPluginEntry[]))]
[JsonSerializable(typeof(CopilotMarketplaceEntry[]))]
internal sealed partial class CopilotPluginJsonContext : JsonSerializerContext;
