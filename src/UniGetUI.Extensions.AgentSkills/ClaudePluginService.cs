using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using UniGetUI.Core.Tools;

namespace UniGetUI.Extensions.AgentSkills;

/// <summary>Claude owns plugin installation, settings, dependencies and trust policy. No plugin files are copied by UniGetUI.</summary>
public sealed class ClaudePluginService : IAgentPluginService
{
    public string Name => "Claude Code";
    public string Guidance => CoreTools.Translate("Native Claude Code plugins for your user account, shared by the Claude Code CLI and the Code tab in Claude Desktop. Uses claude, or the copy bundled with Claude Desktop. Cowork and organization plugins are managed in the Claude app. These are separate from cross-agent skills.");
    public string RemovalWarning => CoreTools.Translate("Remove this marketplace from your Claude user settings? If this is its last declaration, Claude also uninstalls its plugins and deletes their saved data, options and secrets. Refresh the marketplace instead to keep them.");
    public string RestartNotice => CoreTools.Translate("Restart active Claude Code sessions, including the Code tab in Claude Desktop, to apply plugin changes.");
    public bool SupportsUpdate => true;
    public bool SupportsToggle => true;
    public bool SupportsScopedBrowse => true;
    // Claude has no per-marketplace listing, so marketplaces browsed together share one --available read.
    private static readonly TimeSpan AvailableCatalogLifetime = TimeSpan.FromSeconds(30);
    private readonly IAgentPluginRunner _runner;
    private readonly Lock _availableLock = new();
    private Task<ClaudePluginList>? _available;
    private long _availableStarted;
    public ClaudePluginService() : this(new AgentPluginProcessRunner()) { }
    internal ClaudePluginService(IAgentPluginRunner runner) => _runner = runner;

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

    internal static AgentPluginCatalog ParseInstalledCatalog(string plugins, string marketplaces)
    {
        // Without --available, Claude prints a bare array of installed plugins rather than { installed, available }.
        var installedPlugins = plugins.TrimStart().StartsWith('[')
            ? Deserialize(plugins, ClaudePluginJsonContext.Default.ClaudeInstalledPluginArray)
            : Deserialize(plugins, ClaudePluginJsonContext.Default.ClaudePluginList)?.Installed;
        var markets = Deserialize(marketplaces, ClaudePluginJsonContext.Default.ClaudeMarketplaceEntryArray)
            ?? throw new JsonException(CoreTools.Translate("Claude returned an empty marketplace inventory."));
        if (installedPlugins is null)
            throw new JsonException(CoreTools.Translate("Claude returned an unsupported plugin inventory. Update Claude Code and try again."));
        var installed = installedPlugins.Where(item => item.Scope == "user").Select(item =>
        {
            string id = RequirePluginId(item.Id ?? "");
            string description = item.Errors is { Length: > 0 } ? string.Join(Environment.NewLine, item.Errors) : "";
            return new AgentPluginItem(id, description, item.Version ?? "", true, item.Enabled);
        }).ToArray();
        return new(installed,
            markets.Select(item => new AgentPluginMarketplace(RequireValue(item.Name ?? ""), item.Url ?? item.Source ?? ""))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task<AgentPluginCatalog> GetMarketplaceCatalogAsync(string marketplace, CancellationToken token = default)
    {
        string name = RequireValue(marketplace);
        ClaudePluginList catalog = await GetAvailableAsync().WaitAsync(token);
        string suffix = "@" + name;
        Dictionary<string, AgentPluginItem> items = new(StringComparer.Ordinal);
        foreach (var item in catalog.Available!.Where(item => item.PluginId?.EndsWith(suffix, StringComparison.Ordinal) == true))
        {
            string id = RequirePluginId(item.PluginId!);
            items[id] = new(id, item.Description ?? "", item.Version ?? "", false, false);
        }
        // Claude leaves installed plugins out of the available list; keep them so an uninstall leaves an installable row.
        foreach (var item in catalog.Installed!.Where(item => item.Scope == "user" && item.Id?.EndsWith(suffix, StringComparison.Ordinal) == true))
        {
            string id = RequirePluginId(item.Id!);
            items.TryAdd(id, new(id, "", item.Version ?? "", false, false));
        }
        return new(items.Values.OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToArray(), []);
    }

    // The shared read is not tied to one caller's token (the runner bounds it), so one cancelled browse cannot fail another.
    private Task<ClaudePluginList> GetAvailableAsync()
    {
        lock (_availableLock)
        {
            if (_available is null || _available.IsFaulted || _available.IsCanceled
                || Stopwatch.GetElapsedTime(_availableStarted) > AvailableCatalogLifetime)
            {
                _available = ReadAvailableAsync(CancellationToken.None);
                _availableStarted = Stopwatch.GetTimestamp();
            }
            return _available;
        }
    }

    private async Task<ClaudePluginList> ReadAvailableAsync(CancellationToken token)
    {
        string plugins = await _runner.RunAsync(["plugin", "list", "--json", "--available"], token);
        var catalog = Deserialize(plugins, ClaudePluginJsonContext.Default.ClaudePluginList)
            ?? throw new JsonException(CoreTools.Translate("Claude returned an empty plugin inventory."));
        if (catalog.Installed is null || catalog.Available is null)
            throw new JsonException(CoreTools.Translate("Claude returned an unsupported plugin inventory. Update Claude Code and try again."));
        return catalog;
    }

    private async Task<string> MutateAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        try { return await _runner.RunAsync(args, token); }
        finally
        {
            lock (_availableLock) _available = null;
        }
    }

    internal static AgentPluginCatalog ParseCatalog(string plugins, string marketplaces)
    {
        var catalog = Deserialize(plugins, ClaudePluginJsonContext.Default.ClaudePluginList)
            ?? throw new JsonException(CoreTools.Translate("Claude returned an empty plugin inventory."));
        var markets = Deserialize(marketplaces, ClaudePluginJsonContext.Default.ClaudeMarketplaceEntryArray)
            ?? throw new JsonException(CoreTools.Translate("Claude returned an empty marketplace inventory."));
        if (catalog.Installed is null || catalog.Available is null)
            throw new JsonException(CoreTools.Translate("Claude returned an unsupported plugin inventory. Update Claude Code and try again."));
        Dictionary<string, AgentPluginItem> items = new(StringComparer.Ordinal);
        foreach (var item in catalog.Available)
        {
            string id = RequirePluginId(item.PluginId ?? "");
            items[id] = new(id, item.Description ?? "", item.Version ?? "", false, false);
        }
        foreach (var item in catalog.Installed.Where(item => item.Scope == "user"))
        {
            string id = RequirePluginId(item.Id ?? "");
            string description = items.GetValueOrDefault(id)?.Description ?? "";
            if (item.Errors is { Length: > 0 })
                description = string.Join(Environment.NewLine, new[] { description }.Concat(item.Errors).Where(line => line.Length > 0));
            items[id] = new(id, description, item.Version ?? "", true, item.Enabled);
        }
        return new(items.Values.OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToArray(),
            markets.Select(item => new AgentPluginMarketplace(RequireValue(item.Name ?? ""), item.Url ?? item.Source ?? ""))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public Task<string> AddMarketplaceAsync(string input, CancellationToken token = default)
    {
        string source = StripCommand(input, "/plugin marketplace add ");
        return MutateAsync(["plugin", "marketplace", "add", RequireValue(source), "--scope", "user"], token);
    }

    public Task<string> UpdateMarketplaceAsync(string name, CancellationToken token = default) =>
        MutateAsync(["plugin", "marketplace", "update", RequireValue(name)], token);

    public Task<string> RemoveMarketplaceAsync(string name, CancellationToken token = default) =>
        MutateAsync(["plugin", "marketplace", "remove", RequireValue(name), "--scope", "user"], token);

    public Task<string> PerformAsync(string input, AgentPluginAction action, CancellationToken token = default)
    {
        string id = RequirePluginId(StripCommand(input, "/plugin install "));
        string command = action switch
        {
            AgentPluginAction.Install => "install",
            AgentPluginAction.Update => "update",
            AgentPluginAction.Enable => "enable",
            AgentPluginAction.Disable => "disable",
            AgentPluginAction.Uninstall => "uninstall",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        // Never accept marketplace-declared shell commands automatically (--yes / --accept-command).
        return MutateAsync(["plugin", command, id, "--scope", "user"], token);
    }

    private static string StripCommand(string input, string prefix) =>
        input.Trim().StartsWith(prefix, StringComparison.Ordinal)
            ? input.Trim()[prefix.Length..].Trim() : input.Trim();

    private static T? Deserialize<T>(string json, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Deserialize(json, typeInfo);

    private static string RequireValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-') || value.Any(char.IsControl))
            throw new ArgumentException(CoreTools.Translate("Enter a valid Claude marketplace or plugin identifier."));
        return value;
    }

    private static string RequirePluginId(string value)
    {
        RequireValue(value);
        string[] parts = value.Split('@');
        if (parts.Length != 2 || parts.Any(part => part.Length == 0
                || part.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.')))
            throw new ArgumentException(CoreTools.Translate("Enter a plugin identifier in name@marketplace format."));
        return value;
    }
}

internal sealed class ClaudePluginList
{
    public ClaudeInstalledPlugin[]? Installed { get; set; }
    public ClaudeAvailablePlugin[]? Available { get; set; }
}

internal sealed class ClaudeInstalledPlugin
{
    public string? Id { get; set; }
    public string? Version { get; set; }
    public string? Scope { get; set; }
    public bool Enabled { get; set; }
    public string[]? Errors { get; set; }
}

internal sealed class ClaudeAvailablePlugin
{
    public string? PluginId { get; set; }
    public string? Description { get; set; }
    public string? Version { get; set; }
}

internal sealed class ClaudeMarketplaceEntry
{
    public string? Name { get; set; }
    public string? Source { get; set; }
    public string? Url { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ClaudePluginList))]
[JsonSerializable(typeof(ClaudeInstalledPlugin[]))]
[JsonSerializable(typeof(ClaudeMarketplaceEntry[]))]
internal sealed partial class ClaudePluginJsonContext : JsonSerializerContext;
