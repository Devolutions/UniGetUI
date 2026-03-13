using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Manager;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public partial class InternetSettingsView : UserControl, ISettingsSectionView
{
    private readonly DispatcherTimer _credentialsSaveTimer;
    private readonly DispatcherTimer _proxyUrlSaveTimer;
    private bool _isLoading;

    private CheckBox EnableProxyCheckBoxControl => GetControl<CheckBox>("EnableProxyCheckBox");

    private TextBox ProxyUrlTextBoxControl => GetControl<TextBox>("ProxyUrlTextBox");

    private CheckBox EnableProxyAuthCheckBoxControl => GetControl<CheckBox>("EnableProxyAuthCheckBox");

    private TextBox UsernameTextBoxControl => GetControl<TextBox>("UsernameTextBox");

    private TextBox PasswordTextBoxControl => GetControl<TextBox>("PasswordTextBox");

    private TextBlock CredentialsStateText => GetControl<TextBlock>("CredentialsStateBlock");

    private StackPanel CompatibilityRowsPanelControl => GetControl<StackPanel>("CompatibilityRowsPanel");

    private CheckBox WaitForInternetCheckBoxControl => GetControl<CheckBox>("WaitForInternetCheckBox");

    private TextBlock LeadTitleText => GetControl<TextBlock>("LeadTitleBlock");

    private TextBlock LeadDescriptionText => GetControl<TextBlock>("LeadDescriptionBlock");

    private TextBlock ProxyTitleText => GetControl<TextBlock>("ProxyTitleBlock");

    private TextBlock ProxyDescriptionText => GetControl<TextBlock>("ProxyDescriptionBlock");

    private TextBlock ProxyUrlLabelText => GetControl<TextBlock>("ProxyUrlLabelBlock");

    private TextBlock ProxyHintText => GetControl<TextBlock>("ProxyHintBlock");

    private TextBlock AuthTitleText => GetControl<TextBlock>("AuthTitleBlock");

    private TextBlock AuthDescriptionText => GetControl<TextBlock>("AuthDescriptionBlock");

    private TextBlock UsernameLabelText => GetControl<TextBlock>("UsernameLabelBlock");

    private TextBlock PasswordLabelText => GetControl<TextBlock>("PasswordLabelBlock");

    private TextBlock CompatibilityTitleText => GetControl<TextBlock>("CompatibilityTitleBlock");

    private TextBlock CompatibilityDescriptionText => GetControl<TextBlock>("CompatibilityDescriptionBlock");

    private TextBlock ConnectivityTitleText => GetControl<TextBlock>("ConnectivityTitleBlock");

    private TextBlock ConnectivityDescriptionText => GetControl<TextBlock>("ConnectivityDescriptionBlock");

    private TextBlock ConnectivityHintText => GetControl<TextBlock>("ConnectivityHintBlock");

    public InternetSettingsView()
    {
        _credentialsSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _credentialsSaveTimer.Tick += CredentialsSaveTimer_OnTick;

        _proxyUrlSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _proxyUrlSaveTimer.Tick += ProxyUrlSaveTimer_OnTick;

        InitializeComponent();
        EnableProxyCheckBoxControl.Click += EnableProxyCheckBox_OnClick;
        EnableProxyAuthCheckBoxControl.Click += EnableProxyAuthCheckBox_OnClick;
        WaitForInternetCheckBoxControl.Click += WaitForInternetCheckBox_OnClick;

        SectionTitle = CoreTools.Translate("Internet connection settings");
        SectionSubtitle = CoreTools.Translate("Proxy settings, etc.");
        SectionStatus = CoreTools.Translate("Show the live output");

        ApplyLocalizedText();
        PopulateCompatibilityRows();
        LoadStoredValues();

        ProxyUrlTextBoxControl.TextChanged += ProxyUrlTextBox_OnTextChanged;
        UsernameTextBoxControl.TextChanged += CredentialsTextBox_OnTextChanged;
        PasswordTextBoxControl.TextChanged += CredentialsTextBox_OnTextChanged;
    }

    public string SectionTitle { get; }

    public string SectionSubtitle { get; }

    public string SectionStatus { get; }

    private void ApplyLocalizedText()
    {
        LeadTitleText.Text = CoreTools.Translate("Internet connection settings");
        LeadDescriptionText.Text = CoreTools.Translate("Proxy settings, etc.");
        ProxyTitleText.Text = CoreTools.Translate("Proxy settings");
        ProxyDescriptionText.Text = CoreTools.Translate("Proxy settings, etc.");
        EnableProxyCheckBoxControl.Content = CoreTools.Translate("Connect the internet using a custom proxy");
        ProxyUrlLabelText.Text = CoreTools.Translate("Proxy URL");
        ProxyUrlTextBoxControl.Watermark = CoreTools.Translate("Enter proxy URL here");
        ProxyHintText.Text = CoreTools.Translate("Proxy compatibility table");
        AuthTitleText.Text = CoreTools.Translate("Compatible with authentication");
        AuthDescriptionText.Text = CoreTools.Translate("It is not guaranteed that the provided credentials will be stored safely, so you may as well not use the credentials of your bank account");
        EnableProxyAuthCheckBoxControl.Content = CoreTools.Translate("Authenticate to the proxy with an user and a password");
        UsernameLabelText.Text = CoreTools.Translate("Username");
        UsernameTextBoxControl.Watermark = CoreTools.Translate("Username");
        PasswordLabelText.Text = CoreTools.Translate("Password");
        PasswordTextBoxControl.Watermark = CoreTools.Translate("Password");
        CompatibilityTitleText.Text = CoreTools.Translate("Proxy compatibility table");
        CompatibilityDescriptionText.Text = CoreTools.Translate("Compatible with proxy");
        ConnectivityTitleText.Text = CoreTools.Translate("Internet connection settings");
        ConnectivityDescriptionText.Text = CoreTools.Translate("Wait for the device to be connected to the internet before attempting to do tasks that require internet connectivity.");
        WaitForInternetCheckBoxControl.Content = CoreTools.Translate("Wait for the device to be connected to the internet before attempting to do tasks that require internet connectivity.");
        ConnectivityHintText.Text = CoreTools.Translate("Wait for the device to be connected to the internet before attempting to do tasks that require internet connectivity.");
    }

    private void LoadStoredValues()
    {
        _isLoading = true;

        EnableProxyCheckBoxControl.IsChecked = Settings.Get(Settings.K.EnableProxy);
        ProxyUrlTextBoxControl.Text = Settings.GetValue(Settings.K.ProxyURL);
        EnableProxyAuthCheckBoxControl.IsChecked = Settings.Get(Settings.K.EnableProxyAuth);

        var credentials = Settings.GetProxyCredentials();
        UsernameTextBoxControl.Text = credentials?.UserName ?? string.Empty;
        PasswordTextBoxControl.Text = credentials?.Password ?? string.Empty;
        WaitForInternetCheckBoxControl.IsChecked = !Settings.Get(Settings.K.DisableWaitForInternetConnection);

        ApplyInternetControlState();
        _isLoading = false;
    }

    private void EnableProxyCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        Settings.Set(Settings.K.EnableProxy, EnableProxyCheckBoxControl.IsChecked == true);
        ApplyInternetControlState();
        MainWindow.ApplyProxyVariableToProcess();
    }

    private void ProxyUrlTextBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        _proxyUrlSaveTimer.Stop();
        _proxyUrlSaveTimer.Start();
    }

    private void ProxyUrlSaveTimer_OnTick(object? sender, EventArgs e)
    {
        _proxyUrlSaveTimer.Stop();
        if (_isLoading)
        {
            return;
        }

        Settings.SetValue(Settings.K.ProxyURL, ProxyUrlTextBoxControl.Text ?? string.Empty);
        MainWindow.ApplyProxyVariableToProcess();
    }

    private void EnableProxyAuthCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        Settings.Set(Settings.K.EnableProxyAuth, EnableProxyAuthCheckBoxControl.IsChecked == true);
        ApplyInternetControlState();
        MainWindow.ApplyProxyVariableToProcess();
    }

    private void CredentialsTextBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        if (!UsernameTextBoxControl.IsFocused && !PasswordTextBoxControl.IsFocused)
        {
            SetCredentialsStatusText();
            return;
        }

        SetCredentialsStatusText(CoreTools.Translate("Saving packages, please wait..."));
        _credentialsSaveTimer.Stop();
        _credentialsSaveTimer.Start();
    }

    private void CredentialsSaveTimer_OnTick(object? sender, EventArgs e)
    {
        _credentialsSaveTimer.Stop();

        if (_isLoading)
        {
            return;
        }

        if (EnableProxyCheckBoxControl.IsChecked != true || EnableProxyAuthCheckBoxControl.IsChecked != true)
        {
            SetCredentialsStatusText();
            return;
        }

        Settings.SetProxyCredentials(
            UsernameTextBoxControl.Text ?? string.Empty,
            PasswordTextBoxControl.Text ?? string.Empty
        );
        MainWindow.ApplyProxyVariableToProcess();
        SetCredentialsStatusText(CoreTools.Translate("Success!"));
    }

    private void WaitForInternetCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        Settings.Set(Settings.K.DisableWaitForInternetConnection, WaitForInternetCheckBoxControl.IsChecked != true);
    }

    private void ApplyInternetControlState()
    {
        var proxyEnabled = EnableProxyCheckBoxControl.IsChecked == true;
        ProxyUrlTextBoxControl.IsEnabled = proxyEnabled;
        EnableProxyAuthCheckBoxControl.IsEnabled = proxyEnabled;

        var authEnabled = proxyEnabled && EnableProxyAuthCheckBoxControl.IsChecked == true;
        UsernameTextBoxControl.IsEnabled = authEnabled;
        PasswordTextBoxControl.IsEnabled = authEnabled;
        CredentialsStateText.IsEnabled = authEnabled;

        SetCredentialsStatusText();
    }

    private void SetCredentialsStatusText(string? overrideText = null)
    {
        if (
            !string.IsNullOrWhiteSpace(overrideText)
            && EnableProxyCheckBoxControl.IsChecked == true
            && EnableProxyAuthCheckBoxControl.IsChecked == true
        )
        {
            CredentialsStateText.Text = overrideText;
        }
        else if (EnableProxyCheckBoxControl.IsChecked != true)
        {
            CredentialsStateText.Text = CoreTools.Translate("Proxy settings, etc.");
        }
        else if (EnableProxyAuthCheckBoxControl.IsChecked != true)
        {
            CredentialsStateText.Text = CoreTools.Translate("Compatible with authentication");
        }
        else if (_credentialsSaveTimer.IsEnabled)
        {
            CredentialsStateText.Text = CoreTools.Translate("Saving packages, please wait...");
        }
        else
        {
            CredentialsStateText.Text = CoreTools.Translate("It is not guaranteed that the provided credentials will be stored safely, so you may as well not use the credentials of your bank account");
        }
    }

    private void PopulateCompatibilityRows()
    {
        CompatibilityRowsPanelControl.Children.Clear();
        CompatibilityRowsPanelControl.Children.Add(CreateCompatibilityHeader());

        foreach (var manager in PEInterface.Managers)
        {
            CompatibilityRowsPanelControl.Children.Add(CreateCompatibilityRow(manager));
        }
    }

    private static Control CreateCompatibilityHeader()
    {
        var proxyLabel = new TextBlock
        {
            Text = CoreTools.Translate("Compatible with proxy"),
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var authLabel = new TextBlock
        {
            Text = CoreTools.Translate("Compatible with authentication"),
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetColumn(proxyLabel, 1);
        Grid.SetColumn(authLabel, 2);
        return new Border
        {
            Padding = new Thickness(14, 4, 14, 4),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("2*,Auto,Auto"),
                ColumnSpacing = 12,
                Children = { proxyLabel, authLabel },
            },
        };
    }

    private static Control CreateCompatibilityRow(IPackageManager manager)
    {
        var proxySupport = manager.Capabilities.SupportsProxy;
        var (_, executablePath) = manager.GetExecutableFile();
        var proxyBadge = CreateBadge(
            proxySupport switch
            {
                ProxySupport.Yes => CoreTools.Translate("Yes"),
                ProxySupport.Partially => CoreTools.Translate("Partially"),
                _ => CoreTools.Translate("No"),
            },
            proxySupport switch
            {
                ProxySupport.Yes => "success-panel",
                ProxySupport.Partially => "warning-panel",
                _ => "danger-panel",
            }
        );

        var authBadge = CreateBadge(
            manager.Capabilities.SupportsProxyAuth ? CoreTools.Translate("Yes") : CoreTools.Translate("No"),
            manager.Capabilities.SupportsProxyAuth ? "success-panel" : "danger-panel"
        );

        Grid.SetColumn(proxyBadge, 1);
        Grid.SetRowSpan(proxyBadge, 2);
        Grid.SetColumn(authBadge, 2);
        Grid.SetRowSpan(authBadge, 2);

        return new Border
        {
            Classes = { "surface-card", "subtle-panel" },
            Padding = new Thickness(14),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("2*,Auto,Auto"),
                ColumnSpacing = 12,
                RowDefinitions = new RowDefinitions("Auto,Auto"),
                Children =
                {
                    new TextBlock
                    {
                        Text = manager.DisplayName,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    CreateExecutablePathBlock(executablePath),
                    proxyBadge,
                    authBadge,
                }
            }
        };
    }

    private static Border CreateBadge(string text, string panelClass)
    {
        var badge = new Border
        {
            Padding = new Thickness(10, 4),
            Classes = { "surface-card", "badge-panel", panelClass },
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
            },
        };

        return badge;
    }

    private static TextBlock CreateExecutablePathBlock(string executablePath)
    {
        var pathBlock = new TextBlock
        {
            Opacity = 0.72,
            Classes = { "mono-data" },
            Text = string.IsNullOrWhiteSpace(executablePath)
                ? CoreTools.Translate("Please wait...")
                : executablePath,
            TextWrapping = TextWrapping.Wrap,
        };

        Grid.SetRow(pathBlock, 1);
        return pathBlock;
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
