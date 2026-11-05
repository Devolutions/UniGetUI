using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Extensions.AgentSkills;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

internal sealed class AgentSkillsSourcesControl : UserControl
{
    private readonly AgentSkillsService _service;
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly TextBox _address = new() { PlaceholderText = CoreTools.Translate("owner/repo, repository URL, or skills index") };
    private readonly TextBlock _status = new() { TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _empty = new() { Text = CoreTools.Translate("No sources added yet."), TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _add = new() { Content = CoreTools.Translate("Add source") };
    private readonly Button _cancel = new() { Content = CoreTools.Translate("Cancel"), IsVisible = false };
    private CancellationTokenSource? _operation;
    public event EventHandler? SourcesChanged;

    public AgentSkillsSourcesControl(AgentSkillsService service)
    {
        _service = service;
        AutomationProperties.SetName(_address, CoreTools.Translate("Repository or skills index URL"));
        var editor = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(_add, 1);
        editor.Children.Add(_address);
        editor.Children.Add(_add);
        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _rows, _empty,
                new TextBlock { Text = CoreTools.Translate("Add a GitHub repository (owner/repo), git URL, HTTPS skills index, or Notion source."),
                    TextWrapping = global::Avalonia.Media.TextWrapping.Wrap },
                editor, _cancel, _status,
            },
        };
        _add.Click += async (_, _) => await AddAsync();
        _cancel.Click += (_, _) => _operation?.Cancel();
        _address.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await AddAsync();
        };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Reload();
    }
    internal void FocusAddress() => _address.Focus();

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
            var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
            Grid.SetColumn(remove, 1);
            row.Children.Add(new TextBlock
            {
                Text = source.InstallSource,
                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(remove);
            _rows.Children.Add(row);
        }
        _empty.IsVisible = _rows.Children.Count == 0;
    }
    private async Task AddAsync()
    {
        if (_operation is not null) return;
        using var cancel = new CancellationTokenSource();
        _operation = cancel;
        _add.IsEnabled = false;
        _address.IsEnabled = false;
        _cancel.IsVisible = true;
        try
        {
            var source = SkillSourceLocator.ParseUserInput(_address.Text ?? "")
                ?? throw new ArgumentException(CoreTools.Translate(
                    "Enter a GitHub repository (owner/repo), a git repository URL, an HTTPS skills index, or a Notion database."));
            if (AgentSkillsService.FindConfiguredSource(source) is { } existing)
            {
                _status.Text = CoreTools.Translate("{0} is already one of your sources.", existing.Name);
                AccessibilityAnnouncementService.Announce(_status.Text);
                return;
            }
            if (AgentSkillsDependencies.ForSource(source) is { } dependency)
                await AvaloniaBootstrapper.PromptForMissingDependencyAsync(dependency);
            cancel.Token.ThrowIfCancellationRequested();
            var added = await _service.AddSourceAsync(source.InstallSource, new Progress<string>(line =>
            {
                if (!cancel.IsCancellationRequested && ReferenceEquals(_operation, cancel)) _status.Text = line;
            }), cancel.Token);
            _address.Text = "";
            _status.Text = CoreTools.Translate("Added {0}", added.Source.Name);
            AccessibilityAnnouncementService.Announce(_status.Text);
            Reload();
            SourcesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { _status.Text = CoreTools.Translate("Cancelled"); }
        catch (Exception ex) { ShowError(ex); }
        finally { _operation = null; _add.IsEnabled = true; _address.IsEnabled = true; _cancel.IsVisible = false; }
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Reload();
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
