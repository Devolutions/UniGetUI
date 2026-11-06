using System.Diagnostics;
using UniGetUI.Core.Tools;

namespace UniGetUI.Extensions.AgentSkills;

/// <summary>Uses the documented app confirmation handoff as a fallback to native Copilot CLI management.</summary>
public sealed class CopilotAppPluginService : IAgentPluginService
{
    private readonly Action<Uri> _open;
    public string Name => "GitHub Copilot app";
    public string Guidance => CoreTools.Translate("Open a marketplace or plugin confirmation form in the Copilot app. Manage the local installed inventory in GitHub Copilot.");
    public string RemovalWarning => Guidance;
    public string RestartNotice => Guidance;
    public bool SupportsUpdate => false;
    public bool SupportsToggle => false;

    public CopilotAppPluginService() : this(uri =>
    {
        using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    })
    { }
    internal CopilotAppPluginService(Action<Uri> open) => _open = open;

    public Task<AgentPluginCatalog> GetCatalogAsync(CancellationToken token = default) =>
        throw new NotSupportedException(Guidance);
    public Task<string> AddMarketplaceAsync(string input, CancellationToken token = default) =>
        Open("marketplace/add", MarketplaceSource(input), token);
    public Task<string> PerformAsync(string input, AgentPluginAction action, CancellationToken token = default) =>
        action == AgentPluginAction.Install
            ? Open("install", AgentPluginInput.PluginId(CopilotPluginService.InstallSource(input)), token)
            : throw new NotSupportedException(Guidance);
    public Task<string> UpdateMarketplaceAsync(string name, CancellationToken token = default) =>
        throw new NotSupportedException(Guidance);
    public Task<string> RemoveMarketplaceAsync(string name, CancellationToken token = default) =>
        throw new NotSupportedException(Guidance);

    private static string MarketplaceSource(string input)
    {
        string source = CopilotPluginService.MarketplaceSource(input);
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0)
                throw new ArgumentException(CoreTools.Translate("Use owner/repo or an HTTPS Git URL without credentials for the Copilot app."));
            return source;
        }
        string[] parts = source.Split('/');
        if (parts.Length != 2)
            throw new ArgumentException(CoreTools.Translate("Use owner/repo or an HTTPS Git URL without credentials for the Copilot app."));
        foreach (string part in parts) AgentPluginInput.Segment(part);
        return source;
    }

    private Task<string> Open(string action, string source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var link = $"ghapp://plugins/{action}?source={Uri.EscapeDataString(source)}";
        _open(new Uri("https://github.com/copilot/app/launch?open=" + Uri.EscapeDataString(link)));
        return Task.FromResult(CoreTools.Translate("Opened the Copilot app confirmation form. Nothing is installed or added until you confirm in Copilot."));
    }
}
