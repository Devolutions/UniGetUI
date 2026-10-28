using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Extensions.AgentSkills;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

internal sealed class AgentSkillsSourcesControl : UserControl
{
    private readonly AgentSkillsService _service;
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly TextBox _address = new() { PlaceholderText = CoreTools.Translate("Repository or skills index URL") };
    private readonly TextBlock _status = new() { TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _add = new() { Content = CoreTools.Translate("Add source") };
    private readonly Button _cancel = new() { Content = CoreTools.Translate("Cancel"), IsVisible = false };
    private CancellationTokenSource? _operation;
    public event EventHandler? SourcesChanged;

    public AgentSkillsSourcesControl(AgentSkillsService service)
    {
        _service = service;
        AutomationProperties.SetName(_address, CoreTools.Translate("Repository or skills index URL"));
        Content = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 8),
            Children =
            {
                new TextBlock { Text = CoreTools.Translate("Sources"), FontWeight = global::Avalonia.Media.FontWeight.SemiBold },
                _rows, _address, _add, _cancel, _status,
            },
        };
        _add.Click += async (_, _) => await AddAsync();
        _cancel.Click += (_, _) => _operation?.Cancel();
        Reload();
    }
    private void Reload()
    {
        _rows.Children.Clear();
        foreach (var source in AgentSkillsService.GetConfiguredSources())
        {
            var remove = new Button { Content = CoreTools.Translate("Remove"), Tag = source };
            AutomationProperties.SetName(remove, CoreTools.Translate("Remove {0}", source.Name));
            remove.Click += (_, _) =>
            {
                if (_operation is not null) return;
                try { _service.RemoveSource(source); Reload(); SourcesChanged?.Invoke(this, EventArgs.Empty); }
                catch (Exception ex) { ShowError(ex); }
            };
            _rows.Children.Add(new StackPanel
            {
                Spacing = 4,
                Children = { new TextBlock { Text = source.Name, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap }, remove },
            });
        }
    }
    private async Task AddAsync()
    {
        if (_operation is not null) return;
        using var cancel = new CancellationTokenSource();
        _operation = cancel;
        _add.IsEnabled = false;
        _cancel.IsVisible = true;
        try
        {
            var source = SkillSourceLocator.Parse(_address.Text ?? "")
                ?? throw new ArgumentException(CoreTools.Translate("Enter a valid skill source."));
            if (AgentSkillsDependencies.ForSource(source) is { } dependency)
                await AvaloniaBootstrapper.PromptForMissingDependencyAsync(dependency);
            await _service.AddSourceAsync(source.InstallSource, new Progress<string>(line => _status.Text = line), cancel.Token);
            _address.Text = "";
            _status.Text = CoreTools.Translate("Source added");
            AccessibilityAnnouncementService.Announce(_status.Text);
            Reload();
            SourcesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { _status.Text = CoreTools.Translate("Cancelled"); }
        catch (Exception ex) { ShowError(ex); }
        finally { _operation = null; _add.IsEnabled = true; _cancel.IsVisible = false; }
    }
    private void ShowError(Exception ex)
    {
        Logger.Error(ex);
        _status.Text = ex.Message;
        AccessibilityAnnouncementService.Announce(_status.Text, AutomationLiveSetting.Assertive);
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _operation?.Cancel();
        base.OnDetachedFromVisualTree(e);
    }
}
