using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniGetUI.Core.Tools;

namespace UniGetUI.Extensions.AgentSkills;

/// <summary>Imports complete bundles through Cursor's documented local-plugin directory, not its team marketplace API.</summary>
public sealed class CursorPluginService : IAgentPluginService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _store;
    private readonly string _cursorPlugins;
    private readonly IAgentPluginRunner _git;
    public string Name => "Cursor";
    public string Guidance => CoreTools.Translate("Cursor local imports managed by UniGetUI. Add a GitHub marketplace to browse complete Cursor bundles. Requires Git. This is not a Cursor team marketplace; Enterprise must allow local plugin imports. Existing marketplace installs take precedence.");
    public string RemovalWarning => CoreTools.Translate("Remove this source from UniGetUI? Uninstall its managed Cursor plugins first. Cursor team marketplaces and other local plugins are not changed.");
    public string RestartNotice => CoreTools.Translate("Restart Cursor or run Developer: Reload Window, then check Customize. Enterprise policy may block local plugin imports. Enable or disable components in Cursor.");
    public bool SupportsUpdate => true;
    public bool SupportsToggle => false;

    public CursorPluginService() : this(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniGetUI", "AgentPlugins", "Cursor"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "plugins"),
        new AgentPluginProcessRunner(() => AgentPluginProcessRunner.FindNativeExecutable("git"), TimeSpan.FromMinutes(3), "Git"))
    { }

    internal CursorPluginService(string store, string cursorPlugins, IAgentPluginRunner git)
    {
        _store = Path.GetFullPath(store);
        _cursorPlugins = Path.GetFullPath(cursorPlugins);
        _git = git;
    }

    public Task<AgentPluginCatalog> GetCatalogAsync(CancellationToken token = default) => LockedAsync(async () =>
    {
        var state = await ReadStateAsync(token);
        List<AgentPluginItem> plugins = [];
        foreach (var market in state.Marketplaces)
        {
            foreach (var plugin in ReadMarketplace(SourceRoot(market)))
            {
                string id = plugin.Name + "@" + market.Name;
                bool installed = state.Installed.ContainsKey(id);
                bool missing = installed && !Directory.Exists(InstallRoot(id));
                string? notice = plugin.Error;
                if (missing)
                    notice = CoreTools.Translate("Managed Cursor plugin files are missing. Use Update to restore them or Uninstall to remove the installation record.");
                if (!installed && HasOtherLocalPlugin(plugin.Name, null))
                    notice = CoreTools.Translate("A local Cursor plugin with this name already exists. Manage it in Cursor.");
                plugins.Add(new(id, plugin.Description, installed ? state.Installed[id] : plugin.Version, installed, installed)
                {
                    CanInstall = notice is null,
                    Notice = notice,
                    StatusText = missing ? CoreTools.Translate("Managed plugin files missing")
                        : installed ? CoreTools.Translate("Imported locally; reload Cursor to activate") : null,
                });
            }
        }
        return new AgentPluginCatalog(plugins.OrderBy(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase).ToArray(),
            state.Marketplaces.Select(market => new AgentPluginMarketplace(market.Name, market.Source)).ToArray());
    }, token);

    public Task<string> AddMarketplaceAsync(string input, CancellationToken token = default) => LockedAsync(async () =>
    {
        string source = NormalizeSource(AgentPluginInput.StripCommand(input, "/plugin marketplace add "));
        var state = await ReadStateAsync(token);
        if (state.Marketplaces.Any(item => item.Source == source))
            throw new InvalidOperationException(CoreTools.Translate("This Cursor source is already configured."));
        string directory = Guid.NewGuid().ToString("N");
        string root = Path.Combine(_store, "sources", directory);
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        try
        {
            await _git.RunAsync(["clone", "--depth", "1", "--", source, root], token);
            string name = ReadManifest(root).Name ?? throw new JsonException(CoreTools.Translate("Marketplace name is missing."));
            AgentPluginInput.Segment(name);
            _ = ReadMarketplace(root);
            if (state.Marketplaces.Any(item => item.Name == name))
                throw new InvalidOperationException(CoreTools.Translate("A Cursor source with this marketplace name already exists."));
            state.Marketplaces.Add(new() { Name = name, Source = source, Directory = directory });
            await WriteStateAsync(state, token);
            return CoreTools.Translate("Cursor source added.");
        }
        catch
        {
            DeleteTree(root);
            throw;
        }
    }, token);

    public Task<string> UpdateMarketplaceAsync(string name, CancellationToken token = default) => LockedAsync(async () =>
    {
        var state = await ReadStateAsync(token);
        var market = FindMarket(state, name);
        string directory = Guid.NewGuid().ToString("N");
        string next = Path.Combine(_store, "sources", directory);
        try
        {
            await _git.RunAsync(["clone", "--depth", "1", "--", market.Source, next], token);
            if (ReadManifest(next).Name != market.Name)
                throw new InvalidOperationException(CoreTools.Translate("The marketplace changed its name. The existing source was kept."));
            var plugins = ReadMarketplace(next);
            if (state.Installed.Keys.Any(id => id.EndsWith("@" + market.Name, StringComparison.Ordinal)
                    && !plugins.Any(plugin => id == plugin.Name + "@" + market.Name)))
                throw new InvalidOperationException(CoreTools.Translate("The updated source removed an installed plugin. Uninstall it before refreshing this source."));
            string previous = SourceRoot(market);
            market.Directory = directory;
            await WriteStateAsync(state, token);
            DeleteTree(previous);
            return CoreTools.Translate("Cursor source refreshed. Update installed plugins explicitly to apply the new files.");
        }
        catch
        {
            // Once the state points to the new snapshot, preserve it even if old-cache cleanup failed.
            if (!(await ReadStateAsync(CancellationToken.None)).Marketplaces.Any(item => item.Directory == directory))
                DeleteTree(next);
            throw;
        }
    }, token);

    public Task<string> RemoveMarketplaceAsync(string name, CancellationToken token = default) => LockedAsync(async () =>
    {
        var state = await ReadStateAsync(token);
        var market = FindMarket(state, name);
        if (state.Installed.Keys.Any(id => id.EndsWith("@" + market.Name, StringComparison.Ordinal)))
            throw new InvalidOperationException(CoreTools.Translate("Uninstall this source's managed Cursor plugins before removing the source."));
        state.Marketplaces.Remove(market);
        await WriteStateAsync(state, token);
        DeleteTree(SourceRoot(market));
        return CoreTools.Translate("Cursor source removed.");
    }, token);

    public Task<string> PerformAsync(string input, AgentPluginAction action, CancellationToken token = default) => LockedAsync(async () =>
    {
        string id = AgentPluginInput.PluginId(AgentPluginInput.StripCommand(input, "/plugin install "));
        if (action is not (AgentPluginAction.Install or AgentPluginAction.Update or AgentPluginAction.Uninstall))
            throw new NotSupportedException(CoreTools.Translate("Enable or disable Cursor plugin components in Cursor Customize."));
        var state = await ReadStateAsync(token);
        bool managed = state.Installed.ContainsKey(id);
        if (action != AgentPluginAction.Install && !managed)
            throw new InvalidOperationException(CoreTools.Translate("This Cursor plugin is not managed by UniGetUI."));
        CursorBundle? plugin = null;
        if (action != AgentPluginAction.Uninstall)
        {
            var market = FindMarket(state, id[(id.IndexOf('@') + 1)..]);
            plugin = ReadMarketplace(SourceRoot(market)).SingleOrDefault(item => id == item.Name + "@" + market.Name)
                ?? throw new InvalidOperationException(CoreTools.Translate("The plugin was not found in this marketplace."));
        }
        string destination = InstallRoot(id);
        EnsureNoLinks(_cursorPlugins);
        if (Directory.Exists(destination) && !managed)
            throw new IOException(CoreTools.Translate("The Cursor plugin destination already exists. No files were replaced."));
        if (action != AgentPluginAction.Uninstall && HasOtherLocalPlugin(id[..id.IndexOf('@')], destination))
            throw new IOException(CoreTools.Translate("A local Cursor plugin with this name already exists. Manage it in Cursor."));
        string staging = Path.Combine(_cursorPlugins, ".unigetui-staging", Guid.NewGuid().ToString("N"));
        string next = Path.Combine(staging, "next");
        string backup = Path.Combine(staging, "previous");
        Directory.CreateDirectory(staging);
        bool committed = false;
        bool movedPrevious = false;
        bool movedNext = false;
        try
        {
            if (action != AgentPluginAction.Uninstall)
            {
                var bundle = plugin ?? throw new InvalidOperationException(CoreTools.Translate("The plugin was not found in this marketplace."));
                if (bundle.Error is not null) throw new InvalidOperationException(bundle.Error);
                CopyTree(bundle.Path, next, token);
            }
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(destination))
            {
                EnsureNoLinks(destination);
                Directory.Move(destination, backup);
                movedPrevious = true;
            }
            if (action == AgentPluginAction.Uninstall) state.Installed.Remove(id);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                Directory.Move(next, destination);
                movedNext = true;
                state.Installed[id] = plugin?.Version
                    ?? throw new InvalidOperationException(CoreTools.Translate("The plugin was not found in this marketplace."));
            }
            await WriteStateAsync(state, token);
            committed = true;
            return CoreTools.Translate("Cursor local plugin files changed.");
        }
        finally
        {
            if (!committed)
            {
                if (movedNext) DeleteTree(destination);
                if (movedPrevious) Directory.Move(backup, destination);
            }
            DeleteTree(staging);
        }
    }, token);

    private string SourceRoot(CursorSource market) =>
        Path.Combine(_store, "sources", ValidateDirectory(market.Directory));
    private string InstallRoot(string id) =>
        Path.Combine(_cursorPlugins, "local", "unigetui-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id))));

    internal static string NormalizeSource(string input)
    {
        AgentPluginInput.Value(input);
        string[] parts = input.Split('/');
        if (parts.Length == 2 && parts.All(part => part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            input = "https://github.com/" + input + ".git";
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException(CoreTools.Translate("Enter owner/repo or an HTTPS Git repository URL for a Cursor source."));
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static string ValidateDirectory(string value) =>
        Guid.TryParseExact(value, "N", out _) ? value : throw new JsonException("Invalid Cursor source directory.");
    private static CursorSource FindMarket(CursorPluginState state, string name) =>
        state.Marketplaces.SingleOrDefault(item => item.Name == AgentPluginInput.Segment(name))
        ?? throw new InvalidOperationException(CoreTools.Translate("The marketplace is not configured."));

    private static CursorMarketplaceManifest ReadManifest(string root)
    {
        EnsureNoLinks(root);
        foreach (string relative in new[] { ".cursor-plugin/marketplace.json", ".agents/plugins/marketplace.json", ".claude-plugin/marketplace.json" })
        {
            string path = Contained(root, relative);
            if (!File.Exists(path)) continue;
            var manifest = ReadJson(path, CursorPluginJsonContext.Default.CursorMarketplaceManifest);
            if (manifest.Plugins is null) throw new JsonException(CoreTools.Translate("Marketplace plugins are missing."));
            AgentPluginInput.Segment(manifest.Name ?? "");
            return manifest;
        }
        throw new FileNotFoundException(CoreTools.Translate("No supported marketplace manifest was found in the repository."));
    }

    private static IReadOnlyList<CursorBundle> ReadMarketplace(string root)
    {
        var manifest = ReadManifest(root);
        List<CursorBundle> bundles = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Plugins!)
        {
            string name = AgentPluginInput.Segment(entry.Name ?? "");
            if (!names.Add(name)) throw new JsonException(CoreTools.Translate("The marketplace contains duplicate plugin names."));
            string? relative = entry.Source.ValueKind == JsonValueKind.String ? entry.Source.GetString()
                : entry.Source.ValueKind == JsonValueKind.Object
                    && entry.Source.TryGetProperty("source", out var type) && type.GetString() == "local"
                    && entry.Source.TryGetProperty("path", out var path) ? path.GetString() : null;
            if (relative is null)
            {
                bundles.Add(new(name, "", entry.Description ?? "", "", CoreTools.Translate("Remote plugin sources are not supported by Cursor local import. Use Cursor Customize.")));
                continue;
            }
            string directory = Contained(root, relative);
            string? pluginManifest = FindPluginManifest(directory);
            if (pluginManifest is null)
            {
                bundles.Add(new(name, directory, entry.Description ?? "", "", CoreTools.Translate("This bundle has no Cursor or portable Agent Plugins manifest.")));
                continue;
            }
            var plugin = ReadJson(pluginManifest, CursorPluginJsonContext.Default.CursorBundleManifest);
            if (plugin.Name != name) throw new JsonException(CoreTools.Translate("Plugin and marketplace names do not match."));
            bundles.Add(new(name, directory, plugin.Description ?? entry.Description ?? "", plugin.Version ?? "", null));
        }
        return bundles;
    }

    private static string? FindPluginManifest(string root)
    {
        foreach (string relative in new[] { ".cursor-plugin/plugin.json", "plugin.json" })
        {
            string path = Contained(root, relative);
            if (!File.Exists(path)) continue;
            if (relative == "plugin.json")
            {
                var manifest = ReadJson(path, CursorPluginJsonContext.Default.CursorBundleManifest);
                if (manifest.Schema != "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json")
                    throw new JsonException(CoreTools.Translate("The portable plugin manifest has an unsupported schema."));
            }
            return path;
        }
        return null;
    }

    private bool HasOtherLocalPlugin(string name, string? except)
    {
        string local = Path.Combine(_cursorPlugins, "local");
        EnsureNoLinks(local);
        if (!Directory.Exists(local)) return false;
        foreach (string directory in Directory.EnumerateDirectories(local))
        {
            if (string.Equals(directory, except, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
            string? path = FindPluginManifest(directory);
            if (path is not null && ReadJson(path, CursorPluginJsonContext.Default.CursorBundleManifest).Name == name)
                return true;
        }
        return false;
    }

    private static string Contained(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || Path.IsPathRooted(relative)
            || relative.Replace('\\', '/').Split('/').Any(part => part == ".."))
            throw new IOException(CoreTools.Translate("A plugin path escapes its repository."));
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        string suffix = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(suffix) || suffix == ".." || suffix.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new IOException(CoreTools.Translate("A plugin path escapes its repository."));
        EnsureNoLinks(path);
        return path;
    }

    private static void EnsureNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(CoreTools.Translate("Symbolic links and junctions are not supported for managed Cursor plugins."));
    }

    private static void CopyTree(string source, string destination, CancellationToken token)
    {
        EnsureNoLinks(source);
        Directory.CreateDirectory(destination);
        foreach (string path in Directory.EnumerateFileSystemEntries(source))
        {
            token.ThrowIfCancellationRequested();
            EnsureNoLinks(path);
            if (Path.GetFileName(path) == ".git") continue;
            string target = Path.Combine(destination, Path.GetFileName(path));
            if (Directory.Exists(path)) CopyTree(path, target, token);
            else File.Copy(path, target);
        }
    }

    private static void DeleteTree(string path)
    {
        EnsureNoLinks(path);
        if (!Directory.Exists(path)) return;
        // Never follow repository-controlled links during cleanup.
        foreach (string child in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(child);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(child);
                else File.Delete(child);
            }
            else if ((attributes & FileAttributes.Directory) != 0) DeleteTree(child);
            else { File.SetAttributes(child, FileAttributes.Normal); File.Delete(child); }
        }
        Directory.Delete(path);
    }

    private static T ReadJson<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        EnsureNoLinks(path);
        if (new FileInfo(path).Length > 4 * 1024 * 1024)
            throw new JsonException(CoreTools.Translate("The plugin metadata file is too large."));
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, type) ?? throw new JsonException(CoreTools.Translate("Plugin metadata is empty."));
    }

    private Task<CursorPluginState> ReadStateAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string path = Path.Combine(_store, "state.json");
        EnsureNoLinks(path);
        var state = File.Exists(path) ? ReadJson(path, CursorPluginJsonContext.Default.CursorPluginState) : new CursorPluginState();
        foreach (var market in state.Marketplaces) { AgentPluginInput.Segment(market.Name); ValidateDirectory(market.Directory); NormalizeSource(market.Source); }
        foreach (string id in state.Installed.Keys) AgentPluginInput.PluginId(id);
        return Task.FromResult(state);
    }

    private async Task WriteStateAsync(CursorPluginState state, CancellationToken token)
    {
        EnsureNoLinks(_store);
        Directory.CreateDirectory(_store);
        string temporary = Path.Combine(_store, Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, CursorPluginJsonContext.Default.CursorPluginState), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, Path.Combine(_store, "state.json"), overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<T> LockedAsync<T>(Func<Task<T>> work, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try { return await Task.Run(work, token); }
        finally { Gate.Release(); }
    }
}

internal sealed record CursorBundle(string Name, string Path, string Description, string Version, string? Error);
internal sealed class CursorPluginState
{
    public List<CursorSource> Marketplaces { get; set; } = [];
    public Dictionary<string, string> Installed { get; set; } = new(StringComparer.Ordinal);
}
internal sealed class CursorSource
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string Directory { get; set; } = "";
}
internal sealed class CursorMarketplaceManifest
{
    public string? Name { get; set; }
    public CursorMarketplacePlugin[]? Plugins { get; set; }
}
internal sealed class CursorMarketplacePlugin
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public JsonElement Source { get; set; }
}
internal sealed class CursorBundleManifest
{
    [JsonPropertyName("$schema")] public string? Schema { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Version { get; set; }
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CursorPluginState))]
[JsonSerializable(typeof(CursorMarketplaceManifest))]
[JsonSerializable(typeof(CursorBundleManifest))]
internal sealed partial class CursorPluginJsonContext : JsonSerializerContext;
