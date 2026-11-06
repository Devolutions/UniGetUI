using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using UniGetUI.Core.Tools;

namespace UniGetUI.Extensions.AgentSkills;

public sealed class CodexPluginService : IAgentPluginService
{
    /// <summary>Marketplaces the Codex app provisions itself. Codex restores their plugins, so only toggling is offered.</summary>
    private static readonly HashSet<string> AppMarketplaces = new(StringComparer.Ordinal)
    {
        "openai-bundled", "openai-bundled-alpha", "openai-primary-runtime",
    };
    private readonly IAgentPluginRunner _runner;
    private readonly ICodexConfigWriter _config;
    public string Name => "Codex";
    public string CacheIdentity => $"{Name}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable("CODEX_HOME") ?? "")))}";
    public string Guidance => CoreTools.Translate("Native Codex plugins, shared by the Codex CLI, the Codex app and the IDE extension. Uses codex, or the copy bundled with the Codex app. Codex owns configuration, authentication and policy.");
    public string RemovalWarning => CoreTools.Translate("Remove this marketplace from your Codex configuration? Its plugins may stop loading. Refresh instead to keep the source.");
    public string RestartNotice => CoreTools.Translate("Restart active Codex sessions and the Codex app after plugin changes. Refresh the marketplace to fetch source updates.");
    public bool SupportsUpdate => false;
    public bool SupportsToggle => true;
    public bool SupportsScopedBrowse => true;

    public CodexPluginService() : this(new AgentPluginProcessRunner(
        () => AgentPluginProcessRunner.FindNativeExecutable("codex"), TimeSpan.FromMinutes(3), "Codex"),
        new CodexAppServerConfigWriter())
    { }
    internal CodexPluginService(IAgentPluginRunner runner, ICodexConfigWriter? config = null)
    {
        _runner = runner;
        _config = config ?? new CodexAppServerConfigWriter();
    }

    public async Task<AgentPluginCatalog> GetCatalogAsync(CancellationToken token = default)
    {
        string plugins = await _runner.RunAsync(["plugin", "list", "--json", "--available"], token);
        string marketplaces = await _runner.RunAsync(["plugin", "marketplace", "list", "--json"], token);
        return ParseCatalog(plugins, marketplaces);
    }

    public async Task<AgentPluginCatalog> GetInstalledCatalogAsync(CancellationToken token = default)
    {
        string plugins = await _runner.RunAsync(["plugin", "list", "--json"], token);
        string marketplaces = await _runner.RunAsync(["plugin", "marketplace", "list", "--json"], token);
        return ParseInstalledCatalog(plugins, marketplaces);
    }

    public async Task<AgentPluginCatalog> GetMarketplaceCatalogAsync(string marketplace, CancellationToken token = default)
    {
        string name = AgentPluginInput.Segment(marketplace);
        string output = await _runner.RunAsync(["plugin", "list", "--json", "--available", "--marketplace", name], token);
        var catalog = Deserialize(output, CodexPluginJsonContext.Default.CodexPluginList)
            ?? throw new JsonException(CoreTools.Translate("Codex returned an empty plugin inventory."));
        if (catalog.Available is null)
            throw new JsonException(CoreTools.Translate("Unsupported Codex plugin inventory. Update the Codex CLI and try again."));
        var plugins = catalog.Available.Select(item =>
        {
            string id = AgentPluginInput.PluginId(item.PluginId ?? "");
            if (!string.Equals(id.Split('@')[1], name, StringComparison.Ordinal))
                throw new JsonException(CoreTools.Translate("Codex returned a plugin for the wrong marketplace."));
            return new AgentPluginItem(id, item.Description ?? "", item.Version ?? "", false, false)
            {
                CanInstall = item.InstallPolicy != "NOT_AVAILABLE",
                Notice = item.InstallPolicy == "NOT_AVAILABLE" ? CoreTools.Translate("Unavailable under Codex policy.") : null,
            };
        }).ToArray();
        return new(plugins, []);
    }

    private static AgentPluginCatalog ParseInstalledCatalog(string plugins, string marketplaces)
    {
        var catalog = Deserialize(plugins, CodexPluginJsonContext.Default.CodexPluginList)
            ?? throw new JsonException(CoreTools.Translate("Codex returned an empty plugin inventory."));
        if (catalog.Installed is null)
            throw new JsonException(CoreTools.Translate("Unsupported Codex plugin inventory. Update the Codex CLI and try again."));
        var items = catalog.Installed.Select(item =>
        {
            string id = AgentPluginInput.PluginId(item.PluginId ?? "");
            return ForHost(new AgentPluginItem(id, item.Description ?? "", item.Version ?? "", true, item.Enabled));
        }).ToArray();
        return new(items, ParseMarketplaces(marketplaces));
    }

    private static AgentPluginItem ForHost(AgentPluginItem item) => item.IsInstalled && AppMarketplaces.Contains(item.Marketplace)
        ? item with
        {
            CanManage = false,
            CanToggle = true,
            Notice = item.Notice ?? CoreTools.Translate("Provided by the Codex app, which restores it if removed. You can still turn it on or off."),
        }
        : item;

    internal static AgentPluginCatalog ParseCatalog(string plugins, string marketplaces)
    {
        var catalog = Deserialize(plugins, CodexPluginJsonContext.Default.CodexPluginList)
            ?? throw new JsonException(CoreTools.Translate("Codex returned an empty plugin inventory."));
        var markets = Deserialize(marketplaces, CodexPluginJsonContext.Default.CodexMarketplaceList)
            ?? throw new JsonException(CoreTools.Translate("Codex returned an empty marketplace inventory."));
        if (catalog.Installed is null || catalog.Available is null || markets.Marketplaces is null)
            throw new JsonException(CoreTools.Translate("Unsupported Codex plugin inventory. Update the Codex CLI and try again."));
        Dictionary<string, AgentPluginItem> items = new(StringComparer.Ordinal);
        foreach (var item in catalog.Available.Concat(catalog.Installed))
        {
            string id = AgentPluginInput.PluginId(item.PluginId ?? "");
            items[id] = ForHost(new(id, item.Description ?? "", item.Version ?? "", item.Installed, item.Enabled)
            {
                CanInstall = item.InstallPolicy != "NOT_AVAILABLE",
                Notice = item.InstallPolicy == "NOT_AVAILABLE" ? CoreTools.Translate("Unavailable under Codex policy.") : null,
            });
        }
        return new(items.Values.OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToArray(),
            ParseMarketplaces(marketplaces));
    }

    private static AgentPluginMarketplace[] ParseMarketplaces(string marketplaces)
    {
        var markets = Deserialize(marketplaces, CodexPluginJsonContext.Default.CodexMarketplaceList)
            ?? throw new JsonException(CoreTools.Translate("Codex returned an empty marketplace inventory."));
        if (markets.Marketplaces is null)
            throw new JsonException(CoreTools.Translate("Unsupported Codex marketplace inventory. Update the Codex CLI and try again."));
        return markets.Marketplaces.Select(item => new AgentPluginMarketplace(AgentPluginInput.Segment(item.Name ?? ""),
            item.MarketplaceSource?.Source ?? item.Root ?? "")
        { CanManage = item.MarketplaceSource is not null }).ToArray();
    }

    public Task<string> AddMarketplaceAsync(string input, CancellationToken token = default) =>
        _runner.RunAsync(["plugin", "marketplace", "add",
            AgentPluginInput.Value(AgentPluginInput.StripCommand(input, "codex plugin marketplace add ", "/plugin marketplace add "))], token);
    public Task<string> UpdateMarketplaceAsync(string name, CancellationToken token = default) =>
        _runner.RunAsync(["plugin", "marketplace", "upgrade", AgentPluginInput.Segment(name)], token);
    public Task<string> RemoveMarketplaceAsync(string name, CancellationToken token = default) =>
        _runner.RunAsync(["plugin", "marketplace", "remove", AgentPluginInput.Segment(name)], token);

    private static T? Deserialize<T>(string json, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Deserialize(json, typeInfo);

    public Task<string> PerformAsync(string input, AgentPluginAction action, CancellationToken token = default)
    {
        string id = AgentPluginInput.PluginId(AgentPluginInput.StripCommand(input,
            "codex plugin add ", "/plugin install "));
        if (action is AgentPluginAction.Enable or AgentPluginAction.Disable)
            return _config.SetPluginEnabledAsync(id, action == AgentPluginAction.Enable, token);
        string command = action switch
        {
            AgentPluginAction.Install => "add",
            AgentPluginAction.Uninstall => "remove",
            _ => throw new NotSupportedException(CoreTools.Translate("Manage this plugin action in Codex. The Codex CLI does not expose it.")),
        };
        return _runner.RunAsync(["plugin", command, id], token);
    }
}

internal sealed class CodexPluginList
{
    public CodexPluginEntry[]? Installed { get; set; }
    public CodexPluginEntry[]? Available { get; set; }
}
internal sealed class CodexPluginEntry
{
    public string? PluginId { get; set; }
    public string? Description { get; set; }
    public string? Version { get; set; }
    public bool Installed { get; set; }
    public bool Enabled { get; set; }
    public string? InstallPolicy { get; set; }
}
internal sealed class CodexMarketplaceList
{
    public CodexMarketplaceEntry[]? Marketplaces { get; set; }
}
internal sealed class CodexMarketplaceEntry
{
    public string? Name { get; set; }
    public string? Root { get; set; }
    public CodexMarketplaceSource? MarketplaceSource { get; set; }
}
internal sealed class CodexMarketplaceSource
{
    public string? Source { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CodexPluginList))]
[JsonSerializable(typeof(CodexMarketplaceList))]
internal sealed partial class CodexPluginJsonContext : JsonSerializerContext;
