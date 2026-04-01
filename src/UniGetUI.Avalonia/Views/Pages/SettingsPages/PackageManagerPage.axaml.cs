using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Avalonia.Views.Controls;
using UniGetUI.Avalonia.Views.Controls.Settings;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.VcpkgManager;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;
using CornerRadius = global::Avalonia.CornerRadius;
using Thickness = global::Avalonia.Thickness;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class PackageManagerPage : UserControl, ISettingsPage
{
    private PackageManagerViewModel ViewModel => (PackageManagerViewModel)DataContext!;

    public bool CanGoBack => true;
    public string ShortTitle => ViewModel.PageTitle;

    public event EventHandler? RestartRequired;
    public event EventHandler<Type>? NavigationRequested;

    public PackageManagerPage(IPackageManager manager)
    {
        DataContext = new PackageManagerViewModel(manager);
        InitializeComponent();

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PackageManagerViewModel.Severity)
                                or nameof(PackageManagerViewModel.StatusTitle)
                                or nameof(PackageManagerViewModel.StatusMessage))
                ApplyStatusBrushes();
        };

        ViewModel.RestartRequired += (s, e) => RestartRequired?.Invoke(s, e);
        ViewModel.NavigateToAdministratorRequested += (_, _) => NavigationRequested?.Invoke(this, typeof(Administrator));

        BuildPage();
        ApplyStatusBrushes();
    }

    // ── Dynamic UI construction ───────────────────────────────────────────────

    private void BuildPage()
    {
        var manager = ViewModel.Manager;

        // ── Enable/Disable toggle
        EnableManager.DictionaryName = CoreSettings.K.DisabledManagers;
        EnableManager.KeyName = manager.Name;
        EnableManager.Text = CoreTools.Translate("Enable {pm}").Replace("{pm}", manager.DisplayName);
        ExtraControls.IsEnabled = manager.IsEnabled();
        EnableManager.StateChanged += (_, _) =>
        {
            ExtraControls.IsEnabled = manager.IsEnabled();
            _ = ViewModel.ReloadManagerCommand.ExecuteAsync(null);
        };

        // ── Executable picker card
        bool customPathsAllowed = SecureSettings.Get(SecureSettings.K.AllowCustomManagerPaths);
        var execGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var execHint = new TextBlock
        {
            Text = CoreTools.Translate("Not finding the file you are looking for? Make sure it has been added to path."),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumnSpan(execHint, 2);
        execGrid.Children.Add(execHint);

        var execCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var path in manager.FindCandidateExecutableFiles())
            execCombo.Items.Add(path);

        string savedPath = CoreSettings.GetDictionaryItem<string, string>(CoreSettings.K.ManagerPaths, manager.Name) ?? "";
        if (string.IsNullOrEmpty(savedPath))
        {
            var (found, path) = manager.GetExecutableFile();
            savedPath = found ? path : "";
        }
        execCombo.SelectedItem = savedPath;
        execCombo.IsEnabled = customPathsAllowed;
        execCombo.SelectionChanged += (s, _) =>
        {
            if (s is ComboBox combo && combo.SelectedItem?.ToString() is { Length: > 0 } selected)
                ViewModel.OnExecutableSelected(selected);
        };
        Grid.SetRow(execCombo, 1);
        Grid.SetColumnSpan(execCombo, 2);
        execGrid.Children.Add(execCombo);

        if (!customPathsAllowed)
        {
            var securityWarning = new TextBlock
            {
                Text = CoreTools.Translate("For security reasons, changing the executable file is disabled by default"),
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Classes = { "setting-warning-text" },
            };
            Grid.SetRow(securityWarning, 2);
            execGrid.Children.Add(securityWarning);

            var goToSecureBtn = new Button
            {
                Content = new TextBlock { Text = CoreTools.Translate("Change this"), FontSize = 12, Classes = { "hyperlink" } },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
            };
            goToSecureBtn.Click += (_, _) => ViewModel.NavigateToAdministratorCommand.Execute(null);
            Grid.SetRow(goToSecureBtn, 2);
            Grid.SetColumn(goToSecureBtn, 1);
            execGrid.Children.Add(goToSecureBtn);
        }

        ExecutableHolder.Content = new SettingsCard
        {
            BorderThickness = new Thickness(1, 0, 1, 0),
            CornerRadius = new CornerRadius(0),
            Header = CoreTools.Translate("Select the executable to be used. The following list shows the executables found by UniGetUI"),
            Description = execGrid,
            Margin = new Thickness(0, 2, 0, 2),
        };

        // ── Current path card
        var copyIcon = new SvgIcon
        {
            Path = "avares://UniGetUI.Avalonia/Assets/Symbols/copy.svg",
            Width = 24,
            Height = 24,
        };
        var copyBtn = new Button
        {
            Content = copyIcon,
            Padding = new Thickness(8),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        var pathCard = new SettingsCard
        {
            BorderThickness = new Thickness(1, 0, 1, 1),
            CornerRadius = new CornerRadius(0, 0, 8, 8),
            Header = CoreTools.Translate("Current executable file:"),
            Content = copyBtn,
        };
        var pathLabel = new TextBlock
        {
            FontFamily = new FontFamily("Courier New"),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        };
        pathLabel.Text = ViewModel.PathLabelText;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PackageManagerViewModel.PathLabelText))
                pathLabel.Text = ViewModel.PathLabelText;
        };
        pathCard.Description = pathLabel;
        copyBtn.Click += (_, _) => _ = CopyPathAndFlashIcon(pathLabel.Text, copyBtn, copyIcon);
        PathHolder.Content = pathCard;

        // ── Install options panel
        var installOptions = new InstallOptionsPanel(manager);
        installOptions.NavigateToAdministratorRequested += (_, _) =>
            NavigationRequested?.Invoke(this, typeof(Administrator));
        InstallOptionsHolder.Content = installOptions;

        // ── Disable notifications card
        var disableNotifsCard = new CheckboxCard_Dict
        {
            Text = CoreTools.Translate("Ignore packages from {pm} when showing a notification about updates")
                           .Replace("{pm}", manager.DisplayName),
            DictionaryName = CoreSettings.K.DisabledPackageManagerNotifications,
            ForceInversion = true,
            KeyName = manager.Name,
        };

        BuildExtraControls(disableNotifsCard);

        // ── Logs card
        ManagerLogs.Text = CoreTools.Translate("View {0} logs", manager.DisplayName);
        ManagerLogs.Icon = UniGetUI.Interface.Enums.IconType.Console;
        ManagerLogs.Click += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is Window { DataContext: MainWindowViewModel vm })
                vm.OpenManagerLogs(manager);
        };

        // ── pip AppExecution Alias warning
        if (manager.Name == "Pip")
        {
            ManagerLogs.CornerRadius = new CornerRadius(8, 8, 0, 0);
            AppExecutionAliasWarning.IsVisible = true;
            AppExecutionAliasLabel.Text = CoreTools.Translate(
                "If Python cannot be found or is not listing packages but is installed on the system, " +
                "you may need to disable the \"python.exe\" App Execution Alias in the settings.");
        }
    }

    private void BuildExtraControls(CheckboxCard_Dict disableNotifsCard)
    {
        ExtraControls.Children.Clear();
        var manager = ViewModel.Manager;
        bool managerHasSources = manager.Capabilities.SupportsCustomSources && manager.Name != "vcpkg";

        if (managerHasSources)
        {
            ExtraControls.Children.Add(new SourceManagerCard(manager)
            {
                Margin = new Thickness(0, 0, 0, 16),
            });
            ExtraControls.Children.Add(new TextBlock
            {
                Margin = new Thickness(44, 24, 4, 8),
                FontWeight = FontWeight.SemiBold,
                Text = CoreTools.Translate("Advanced options"),
            });
        }

        switch (manager.Name)
        {
            case "WinGet":
                disableNotifsCard.CornerRadius = new CornerRadius(8, 8, 0, 0);
                disableNotifsCard.BorderThickness = new Thickness(1, 1, 1, 0);
                ExtraControls.Children.Add(disableNotifsCard);

                var wingetResetBtn = new ButtonCard
                {
                    Text = CoreTools.Translate("Reset WinGet")
                        + $" ({CoreTools.Translate("This may help if no packages are listed")})",
                    ButtonText = CoreTools.AutoTranslated("Reset"),
                    CornerRadius = new CornerRadius(0),
                };
                wingetResetBtn.Click += (_, _) => { /* TODO: HandleBrokenWinGet */ };
                ExtraControls.Children.Add(wingetResetBtn);

                ExtraControls.Children.Add(new CheckboxCard
                {
                    Text = CoreTools.Translate("Force install location parameter when updating packages with custom locations"),
                    SettingName = CoreSettings.K.WinGetForceLocationOnUpdate,
                    CornerRadius = new CornerRadius(0),
                    BorderThickness = new Thickness(1, 0, 1, 1),
                });

                var wingetUseBundled = new CheckboxCard
                {
                    Text = $"{CoreTools.Translate("Use bundled WinGet instead of system WinGet")} ({CoreTools.Translate("This may help if WinGet packages are not shown")})",
                    SettingName = CoreSettings.K.ForceLegacyBundledWinGet,
                    CornerRadius = new CornerRadius(0, 0, 8, 8),
                    BorderThickness = new Thickness(1, 0, 1, 1),
                };
                wingetUseBundled.StateChanged += (_, _) => _ = ViewModel.ReloadManagerCommand.ExecuteAsync(null);
                ExtraControls.Children.Add(wingetUseBundled);
                break;

            case "Scoop":
                disableNotifsCard.CornerRadius = new CornerRadius(8, 8, 0, 0);
                disableNotifsCard.BorderThickness = new Thickness(1, 1, 1, 0);
                ExtraControls.Children.Add(disableNotifsCard);

                var scoopInstall = new ButtonCard
                {
                    Text = CoreTools.AutoTranslated("Install Scoop"),
                    ButtonText = CoreTools.AutoTranslated("Install"),
                    CornerRadius = new CornerRadius(0),
                };
                scoopInstall.Click += (_, _) => ViewModel.ScoopInstallCommand.Execute(null);
                ExtraControls.Children.Add(scoopInstall);

                var scoopUninstall = new ButtonCard
                {
                    Text = CoreTools.AutoTranslated("Uninstall Scoop (and its packages)"),
                    ButtonText = CoreTools.AutoTranslated("Uninstall"),
                    CornerRadius = new CornerRadius(0),
                    BorderThickness = new Thickness(1, 0, 1, 0),
                };
                scoopUninstall.Click += (_, _) => ViewModel.ScoopUninstallCommand.Execute(null);
                ExtraControls.Children.Add(scoopUninstall);

                var scoopCleanup = new ButtonCard
                {
                    Text = CoreTools.AutoTranslated("Run cleanup and clear cache"),
                    ButtonText = CoreTools.AutoTranslated("Run"),
                    CornerRadius = new CornerRadius(0),
                };
                scoopCleanup.Click += (_, _) => ViewModel.ScoopCleanupCommand.Execute(null);
                ExtraControls.Children.Add(scoopCleanup);

                ExtraControls.Children.Add(new CheckboxCard
                {
                    CornerRadius = new CornerRadius(0, 0, 8, 8),
                    BorderThickness = new Thickness(1, 0, 1, 1),
                    SettingName = CoreSettings.K.EnableScoopCleanup,
                    Text = CoreTools.AutoTranslated("Enable Scoop cleanup on launch"),
                });
                break;

            case "Chocolatey":
                disableNotifsCard.CornerRadius = new CornerRadius(8, 8, 0, 0);
                disableNotifsCard.BorderThickness = new Thickness(1, 1, 1, 0);
                ExtraControls.Children.Add(disableNotifsCard);

                var chocoSysChoco = new CheckboxCard
                {
                    Text = CoreTools.AutoTranslated("Use system Chocolatey"),
                    SettingName = CoreSettings.K.UseSystemChocolatey,
                    CornerRadius = new CornerRadius(0, 0, 8, 8),
                };
                chocoSysChoco.StateChanged += (_, _) => _ = ViewModel.ReloadManagerCommand.ExecuteAsync(null);
                ExtraControls.Children.Add(chocoSysChoco);
                break;

            case "vcpkg":
                disableNotifsCard.CornerRadius = new CornerRadius(8, 8, 0, 0);
                disableNotifsCard.BorderThickness = new Thickness(1, 1, 1, 0);
                ExtraControls.Children.Add(disableNotifsCard);

                CoreSettings.SetValue(CoreSettings.K.DefaultVcpkgTriplet, Vcpkg.GetDefaultTriplet());
                var vcpkgTriplet = new ComboboxCard
                {
                    Text = CoreTools.Translate("Default vcpkg triplet"),
                    SettingName = CoreSettings.K.DefaultVcpkgTriplet,
                    CornerRadius = new CornerRadius(0),
                };
                foreach (string triplet in Vcpkg.GetSystemTriplets())
                    vcpkgTriplet.AddItem(triplet, triplet, false);
                vcpkgTriplet.ShowAddedItems();
                ExtraControls.Children.Add(vcpkgTriplet);
                ExtraControls.Children.Add(BuildVcpkgRootCard());
                break;

            default:
                disableNotifsCard.CornerRadius = new CornerRadius(8);
                disableNotifsCard.BorderThickness = new Thickness(1);
                ExtraControls.Children.Add(disableNotifsCard);
                break;
        }
    }

    private ButtonCard BuildVcpkgRootCard()
    {
        var vcpkgRootCard = new ButtonCard
        {
            Text = CoreTools.AutoTranslated("Change vcpkg root location"),
            ButtonText = CoreTools.AutoTranslated("Select"),
            CornerRadius = new CornerRadius(0, 0, 8, 8),
            BorderThickness = new Thickness(1, 0, 1, 1),
        };

        var rootLabel = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Text = ViewModel.VcpkgRootPath,
        };
        var resetBtn = new Button
        {
            Content = CoreTools.Translate("Reset"),
            IsEnabled = ViewModel.IsCustomVcpkgRootSet,
            Margin = new Thickness(4, 0),
        };
        var openBtn = new Button
        {
            Content = CoreTools.Translate("Open"),
            IsEnabled = ViewModel.IsCustomVcpkgRootSet,
            Margin = new Thickness(4, 0),
        };

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PackageManagerViewModel.VcpkgRootPath))
                rootLabel.Text = ViewModel.VcpkgRootPath;
            if (e.PropertyName == nameof(PackageManagerViewModel.IsCustomVcpkgRootSet))
            {
                resetBtn.IsEnabled = ViewModel.IsCustomVcpkgRootSet;
                openBtn.IsEnabled = ViewModel.IsCustomVcpkgRootSet;
            }
        };

        resetBtn.Click += (_, _) => ViewModel.ResetVcpkgRootCommand.Execute(null);
        openBtn.Click += (_, _) => ViewModel.OpenVcpkgRootCommand.Execute(null);

        var descPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        descPanel.Children.Add(rootLabel);
        descPanel.Children.Add(resetBtn);
        descPanel.Children.Add(openBtn);
        vcpkgRootCard.Description = descPanel;

        vcpkgRootCard.Command = ViewModel.PickVcpkgRootCommand;
        vcpkgRootCard.CommandParameter = vcpkgRootCard;
        return vcpkgRootCard;
    }

    // ── View-only: brush lookup (needs ActualThemeVariant) ───────────────────

    private void ApplyStatusBrushes()
    {
        StatusBar.Classes.Remove("status-success");
        StatusBar.Classes.Remove("status-warning");
        StatusBar.Classes.Remove("status-error");
        StatusBar.Classes.Remove("status-info");

        string cls = ViewModel.Severity switch
        {
            ManagerStatusSeverity.Success => "status-success",
            ManagerStatusSeverity.Warning => "status-warning",
            ManagerStatusSeverity.Error => "status-error",
            _ => "status-info",
        };
        StatusBar.Classes.Add(cls);
    }

    private async Task CopyPathAndFlashIcon(string? text, Button btn, SvgIcon icon)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
        btn.Content = new TextBlock { Text = "✓", FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
        await Task.Delay(1000);
        btn.Content = icon;
    }
}
