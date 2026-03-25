using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views.Controls.Settings;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class InternetViewModel : ViewModelBase
{
    public event EventHandler? RestartRequired;

    private TextBox? _usernameBox;
    private TextBox? _passwordBox;
    private ProgressBar? _savingIndicator;

    [ObservableProperty] private bool _isProxyEnabled;
    [ObservableProperty] private bool _isProxyAuthEnabled;

    public InternetViewModel()
    {
        _isProxyEnabled = CoreSettings.Get(CoreSettings.K.EnableProxy);
        _isProxyAuthEnabled = CoreSettings.Get(CoreSettings.K.EnableProxyAuth);
    }

    public SettingsCard BuildCredentialsCard()
    {
        _savingIndicator = new ProgressBar
        {
            IsIndeterminate = true,
            Opacity = 0,
            Margin = new Thickness(0, -8, 0, 0),
        };

        _usernameBox = new TextBox
        {
            Watermark = CoreTools.Translate("Username"),
            MinWidth = 200,
            Margin = new Thickness(0, 0, 0, 4),
        };

        _passwordBox = new TextBox
        {
            Watermark = CoreTools.Translate("Password"),
            MinWidth = 200,
            PasswordChar = '●',
        };

        var creds = CoreSettings.GetProxyCredentials();
        if (creds is not null)
        {
            _usernameBox.Text = creds.UserName;
            _passwordBox.Text = creds.Password;
        }

        _usernameBox.TextChanged += (_, _) => _ = SaveCredentialsAsync();
        _passwordBox.TextChanged += (_, _) => _ = SaveCredentialsAsync();

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(_savingIndicator);
        stack.Children.Add(_usernameBox);
        stack.Children.Add(_passwordBox);

        return new SettingsCard
        {
            CornerRadius = new CornerRadius(0, 0, 8, 8),
            BorderThickness = new Thickness(1, 0, 1, 1),
            Header = CoreTools.Translate("Credentials"),
            Description = CoreTools.Translate("It is not guaranteed that the provided credentials will be stored safely"),
            Content = stack,
        };
    }

    public Control BuildProxyCompatTable()
    {
        var noStr = CoreTools.Translate("No");
        var yesStr = CoreTools.Translate("Yes");
        var partStr = CoreTools.Translate("Partially");

        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,*"), Margin = new Thickness(0, 0, 0, 8) };
        headerRow.Children.Add(WithCol(new TextBlock { Text = CoreTools.Translate("Package manager"), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap }, 1));
        headerRow.Children.Add(WithCol(new TextBlock { Text = CoreTools.Translate("Compatible with proxy"), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 0, 0) }, 2));
        headerRow.Children.Add(WithCol(new TextBlock { Text = CoreTools.Translate("Compatible with authentication"), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 0, 0) }, 3));

        var managerCol = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6 };
        var proxyCol = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6 };
        var authCol = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6 };

        foreach (var manager in PEInterface.Managers)
        {
            managerCol.Children.Add(new TextBlock { Text = manager.DisplayName, TextAlignment = TextAlignment.Center });

            var proxyLevel = manager.Capabilities.SupportsProxy;
            proxyCol.Children.Add(StatusBadge(
                proxyLevel is ProxySupport.No ? noStr : (proxyLevel is ProxySupport.Partially ? partStr : yesStr),
                proxyLevel is ProxySupport.Yes ? Colors.Green : (proxyLevel is ProxySupport.Partially ? Colors.Orange : Colors.Red)));

            authCol.Children.Add(StatusBadge(
                manager.Capabilities.SupportsProxyAuth ? yesStr : noStr,
                manager.Capabilities.SupportsProxyAuth ? Colors.Green : Colors.Red));
        }

        var dataRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,*"), ColumnSpacing = 16 };
        dataRow.Children.Add(WithCol(managerCol, 1));
        dataRow.Children.Add(WithCol(proxyCol, 2));
        dataRow.Children.Add(WithCol(authCol, 3));

        var tableStack = new StackPanel { Orientation = Orientation.Vertical };
        tableStack.Children.Add(headerRow);
        tableStack.Children.Add(dataRow);

        return new SettingsCard
        {
            CornerRadius = new CornerRadius(8),
            Header = CoreTools.Translate("Proxy compatibility table"),
            Description = tableStack,
        };
    }

    private static Border StatusBadge(string text, Color color) => new Border
    {
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(4, 2),
        BorderThickness = new Thickness(1),
        Background = new SolidColorBrush(Color.FromArgb(60, color.R, color.G, color.B)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(120, color.R, color.G, color.B)),
        Child = new TextBlock { Text = text, TextAlignment = TextAlignment.Center },
    };

    private static Control WithCol(Control c, int col) { Grid.SetColumn(c, col); return c; }

    private async Task SaveCredentialsAsync()
    {
        if (_usernameBox is null || _passwordBox is null || _savingIndicator is null) return;
        _savingIndicator.Opacity = 1;
        string u = _usernameBox.Text ?? "";
        string p = _passwordBox.Text ?? "";
        await Task.Delay(500);
        if ((_usernameBox.Text ?? "") != u) return;
        if ((_passwordBox.Text ?? "") != p) return;
        CoreSettings.SetProxyCredentials(u, p);
        InternetViewModel.ApplyProxyToProcess();
        _savingIndicator.Opacity = 0;
    }

    [RelayCommand]
    private void RefreshProxyEnabled()
    {
        IsProxyEnabled = CoreSettings.Get(CoreSettings.K.EnableProxy);
        ApplyProxyToProcess();
    }

    [RelayCommand]
    private void RefreshProxyAuthEnabled()
    {
        IsProxyAuthEnabled = CoreSettings.Get(CoreSettings.K.EnableProxyAuth);
        ApplyProxyToProcess();
    }

    [RelayCommand]
    private static void ApplyProxy() => ApplyProxyToProcess();

    [RelayCommand]
    private void ShowRestartRequired() => RestartRequired?.Invoke(this, EventArgs.Empty);

    public static void ApplyProxyToProcess()
    {
        var proxyUri = CoreSettings.GetProxyUrl();
        if (proxyUri is null || !CoreSettings.Get(CoreSettings.K.EnableProxy))
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", "", EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("HTTPS_PROXY", "", EnvironmentVariableTarget.Process);
            return;
        }
        string content;
        if (!CoreSettings.Get(CoreSettings.K.EnableProxyAuth))
        {
            content = proxyUri.ToString();
        }
        else
        {
            var creds = CoreSettings.GetProxyCredentials();
            content = creds is not null
                ? $"{proxyUri.Scheme}://{creds.UserName}:{creds.Password}@{proxyUri.Host}:{proxyUri.Port}"
                : proxyUri.ToString();
        }
        Environment.SetEnvironmentVariable("HTTP_PROXY", content, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("HTTPS_PROXY", content, EnvironmentVariableTarget.Process);
    }
}
