using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Avalonia.Views.Pages.ManagersPages;

public partial class ManagerRowView : UserControl
{
    private bool _isLoading;
    private IPackageManager? _manager;

    private TextBlock AvatarText => GetControl<TextBlock>("AvatarTextBlock");

    private TextBlock TitleText => GetControl<TextBlock>("ManagerTitleBlock");

    private TextBlock DescriptionText => GetControl<TextBlock>("ManagerDescriptionBlock");

    private Border StatusBadge => GetControl<Border>("StatusBadgeBorder");

    private TextBlock StatusText => GetControl<TextBlock>("StatusTextBlock");

    private TextBlock ExecutablePathText => GetControl<TextBlock>("ExecutablePathBlock");

    private CheckBox EnabledToggle => GetControl<CheckBox>("EnabledCheckBox");

    private TextBlock ToggleCaptionText => GetControl<TextBlock>("ToggleCaptionBlock");

    private Button OpenManagerAction => GetControl<Button>("OpenManagerButton");

    public ManagerRowView()
    {
        InitializeComponent();
        EnabledToggle.Content = CoreTools.Translate("Enabled");
        ToggleCaptionText.Text = CoreTools.Translate("Enabled");
    }

    internal event EventHandler<IPackageManager>? NavigationRequested;

    public void LoadManager(IPackageManager manager)
    {
        _manager = manager;

        var displayName = string.IsNullOrWhiteSpace(manager.DisplayName) ? manager.Name : manager.DisplayName;

        var description = manager.Properties.Description.Split("<br>", StringSplitOptions.TrimEntries)[0];
        if (string.IsNullOrWhiteSpace(description))
        {
            description = string.IsNullOrWhiteSpace(manager.Properties.Description)
                ? CoreTools.Translate("Package manager preferences")
                : manager.Properties.Description;
        }

        AvatarText.Text = displayName[..1].ToUpperInvariant();
        TitleText.Text = displayName;
        DescriptionText.Text = description;

        _isLoading = true;
        EnabledToggle.IsChecked = !Settings.GetDictionaryItem<string, bool>(Settings.K.DisabledManagers, manager.Name);
        _isLoading = false;

        RefreshStatus();
    }

    private void RefreshStatus(string? overrideStatus = null)
    {
        if (_manager is null)
        {
            return;
        }

        StatusBadge.Classes.Set("ready", false);
        StatusBadge.Classes.Set("disabled", false);
        StatusBadge.Classes.Set("missing", false);
        StatusBadge.Classes.Set("loading", false);

        if (overrideStatus is not null)
        {
            StatusText.Text = overrideStatus;
            StatusBadge.Classes.Set("loading", true);
            ExecutablePathText.Text = CoreTools.Translate("Loading packages, please wait...");
            return;
        }

        if (!_manager.IsEnabled())
        {
            StatusText.Text = CoreTools.Translate("Disabled");
            StatusBadge.Classes.Set("disabled", true);
        }
        else if (_manager.Status.Found)
        {
            StatusText.Text = CoreTools.Translate("Ready");
            StatusBadge.Classes.Set("ready", true);
        }
        else
        {
            StatusText.Text = CoreTools.Translate("Not found");
            StatusBadge.Classes.Set("missing", true);
        }

        ExecutablePathText.Text = string.IsNullOrWhiteSpace(_manager.Status.ExecutablePath)
            ? CoreTools.Translate("Not found")
            : _manager.Status.ExecutablePath;
    }

    private void OpenManagerButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_manager is not null && !_isLoading)
        {
            NavigationRequested?.Invoke(this, _manager);
        }
    }

    private async void EnabledCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_manager is null || _isLoading)
        {
            return;
        }

        _isLoading = true;
        EnabledToggle.IsEnabled = false;
        OpenManagerAction.IsEnabled = false;
        RefreshStatus(CoreTools.Translate("Please wait..."));

        try
        {
            var disabled = EnabledToggle.IsChecked != true;
            Settings.SetDictionaryItem(Settings.K.DisabledManagers, _manager.Name, disabled);
            await Task.Run(_manager.Initialize);
        }
        finally
        {
            EnabledToggle.IsEnabled = true;
            OpenManagerAction.IsEnabled = true;
            _isLoading = false;
            RefreshStatus();
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private T GetControl<T>(string name)
        where T : Control
    {
        return this.FindControl<T>(name)
            ?? throw new InvalidOperationException($"Control '{name}' was not found.");
    }
}
