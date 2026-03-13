using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class InstallOptionsEditorView : UserControl
{
    private readonly IPackage _package;
    private InstallOptions _options = new();

    public InstallOptionsEditorView()
    {
        _package = null!;
        InitializeComponent();
        IsEnabled = false;
    }

    private TextBlock PackageTitleText => GetControl<TextBlock>("PackageTitleBlock");
    private TextBlock PackageIdText => GetControl<TextBlock>("PackageIdBlock");
    private TextBlock PackageVersionText => GetControl<TextBlock>("PackageVersionBlock");
    private TextBlock PackageManagerText => GetControl<TextBlock>("PackageManagerBlock");
    private TextBlock GeneralOptionsTitleText => GetControl<TextBlock>("GeneralOptionsTitleBlock");
    private CheckBox RunAsAdminCheckBoxControl => GetControl<CheckBox>("RunAsAdminCheckBox");
    private CheckBox InteractiveCheckBoxControl => GetControl<CheckBox>("InteractiveCheckBox");
    private CheckBox HashCheckBoxControl => GetControl<CheckBox>("HashCheckBox");
    private CheckBox AutoUpdateCheckBoxControl => GetControl<CheckBox>("AutoUpdateCheckBox");
    private CheckBox SkipMinorUpdatesCheckBoxControl => GetControl<CheckBox>("SkipMinorUpdatesCheckBox");
    private CheckBox IgnoreUpdatesCheckBoxControl => GetControl<CheckBox>("IgnoreUpdatesCheckBox");
    private CheckBox UninstallPreviousCheckBoxControl => GetControl<CheckBox>("UninstallPreviousCheckBox");
    private TextBlock VersionSectionTitleText => GetControl<TextBlock>("VersionSectionTitleBlock");
    private TextBlock VersionLabelText => GetControl<TextBlock>("VersionLabelBlock");
    private ComboBox VersionComboBoxControl => GetControl<ComboBox>("VersionComboBox");
    private ProgressBar VersionLoadingBarControl => GetControl<ProgressBar>("VersionLoadingBar");
    private TextBlock ArchitectureLabelText => GetControl<TextBlock>("ArchitectureLabelBlock");
    private ComboBox ArchitectureComboBoxControl => GetControl<ComboBox>("ArchitectureComboBox");
    private TextBlock ScopeLabelText => GetControl<TextBlock>("ScopeLabelBlock");
    private ComboBox ScopeComboBoxControl => GetControl<ComboBox>("ScopeComboBox");
    private Border CustomLocationCardControl => GetControl<Border>("CustomLocationCard");
    private TextBlock LocationTitleText => GetControl<TextBlock>("LocationTitleBlock");
    private TextBox CustomLocationTextBoxControl => GetControl<TextBox>("CustomLocationTextBox");
    private Button BrowseLocationButtonControl => GetControl<Button>("BrowseLocationButton");
    private Button ResetLocationButtonControl => GetControl<Button>("ResetLocationButton");
    private Border CustomParametersCardControl => GetControl<Border>("CustomParametersCard");
    private TextBlock ParametersTitleText => GetControl<TextBlock>("ParametersTitleBlock");
    private TextBlock CustomParamsDescText => GetControl<TextBlock>("CustomParamsDescBlock");
    private TextBlock InstallParamsLabelText => GetControl<TextBlock>("InstallParamsLabelBlock");
    private TextBox CustomParams1TextBoxControl => GetControl<TextBox>("CustomParams1TextBox");
    private TextBlock UpdateParamsLabelText => GetControl<TextBlock>("UpdateParamsLabelBlock");
    private TextBox CustomParams2TextBoxControl => GetControl<TextBox>("CustomParams2TextBox");
    private TextBlock UninstallParamsLabelText => GetControl<TextBlock>("UninstallParamsLabelBlock");
    private TextBox CustomParams3TextBoxControl => GetControl<TextBox>("CustomParams3TextBox");
    private TextBlock KillProcessesTitleText => GetControl<TextBlock>("KillProcessesTitleBlock");
    private TextBlock KillProcessesDescText => GetControl<TextBlock>("KillProcessesDescBlock");
    private TextBox KillProcessesTextBoxControl => GetControl<TextBox>("KillProcessesTextBox");
    private CheckBox KillProcessesThatWontDieCheckBoxControl => GetControl<CheckBox>("KillProcessesThatWontDieCheckBox");
    private Border PrePostCommandsCardControl => GetControl<Border>("PrePostCommandsCard");
    private TextBlock PrePostTitleText => GetControl<TextBlock>("PrePostTitleBlock");
    private TextBlock PrePostCommandsDescText => GetControl<TextBlock>("PrePostCommandsDescBlock");
    private TextBlock PreInstallLabelText => GetControl<TextBlock>("PreInstallLabelBlock");
    private TextBox PreInstallCommandBoxControl => GetControl<TextBox>("PreInstallCommandBox");
    private CheckBox AbortInstallCheckBoxControl => GetControl<CheckBox>("AbortInstallCheckBox");
    private TextBlock PostInstallLabelText => GetControl<TextBlock>("PostInstallLabelBlock");
    private TextBox PostInstallCommandBoxControl => GetControl<TextBox>("PostInstallCommandBox");
    private TextBlock PreUpdateLabelText => GetControl<TextBlock>("PreUpdateLabelBlock");
    private TextBox PreUpdateCommandBoxControl => GetControl<TextBox>("PreUpdateCommandBox");
    private CheckBox AbortUpdateCheckBoxControl => GetControl<CheckBox>("AbortUpdateCheckBox");
    private TextBlock PostUpdateLabelText => GetControl<TextBlock>("PostUpdateLabelBlock");
    private TextBox PostUpdateCommandBoxControl => GetControl<TextBox>("PostUpdateCommandBox");
    private TextBlock PreUninstallLabelText => GetControl<TextBlock>("PreUninstallLabelBlock");
    private TextBox PreUninstallCommandBoxControl => GetControl<TextBox>("PreUninstallCommandBox");
    private CheckBox AbortUninstallCheckBoxControl => GetControl<CheckBox>("AbortUninstallCheckBox");
    private TextBlock PostUninstallLabelText => GetControl<TextBlock>("PostUninstallLabelBlock");
    private TextBox PostUninstallCommandBoxControl => GetControl<TextBox>("PostUninstallCommandBox");

    public InstallOptionsEditorView(IPackage package)
    {
        _package = package;
        InitializeComponent();
        ApplyTranslations();
        PopulateStaticControls();
        _ = LoadOptionsAsync();
    }

    public async Task SaveAsync()
    {
        var options = ReadOptionsFromUi();
        await InstallOptionsFactory.SaveForPackageAsync(options, _package);

        Settings.Set(
            Settings.K.KillProcessesThatRefuseToDie,
            KillProcessesThatWontDieCheckBoxControl.IsChecked ?? false);

        if (IgnoreUpdatesCheckBoxControl.IsChecked ?? false)
        {
            await _package.AddToIgnoredUpdatesAsync(version: "*");
        }
        else if (await _package.GetIgnoredUpdatesVersionAsync() == "*")
        {
            await _package.RemoveFromIgnoredUpdatesAsync();
        }
    }

    private void ApplyTranslations()
    {
        PackageTitleText.Text = CoreTools.Translate("{0} installation options", _package.Name);
        PackageIdText.Text = _package.Id;
        PackageVersionText.Text = string.IsNullOrWhiteSpace(_package.VersionString)
            ? CoreTools.Translate("Unknown")
            : _package.VersionString;
        PackageManagerText.Text = _package.Source.AsString_DisplayName;

        GeneralOptionsTitleText.Text = CoreTools.Translate("Installation options");
        RunAsAdminCheckBoxControl.Content = CoreTools.Translate("Install as administrator");
        InteractiveCheckBoxControl.Content = CoreTools.Translate("Interactive installation");
        HashCheckBoxControl.Content = CoreTools.Translate("Skip hash checks");
        AutoUpdateCheckBoxControl.Content = CoreTools.Translate("Automatically update this package");
        SkipMinorUpdatesCheckBoxControl.Content = CoreTools.Translate("Skip minor updates for this package");
        IgnoreUpdatesCheckBoxControl.Content = CoreTools.Translate("Ignore updates for this package");
        UninstallPreviousCheckBoxControl.Content = CoreTools.Translate("Uninstall previous versions when updated");

        KillProcessesTitleText.Text = CoreTools.Translate("Custom arguments:");
        KillProcessesDescText.Text = CoreTools.Translate("Custom command-line arguments:");
        KillProcessesTextBoxControl.Watermark = CoreTools.Translate("Custom arguments:");
        KillProcessesThatWontDieCheckBoxControl.Content = CoreTools.Translate("Abort install if pre-install command fails");

        VersionSectionTitleText.Text = CoreTools.Translate("Installation options");
        VersionLabelText.Text = CoreTools.Translate("Version");
        ArchitectureLabelText.Text = CoreTools.Translate("Architecture to install:");
        ScopeLabelText.Text = CoreTools.Translate("Installation scope:");

        LocationTitleText.Text = CoreTools.Translate("Install location:");
        CustomLocationTextBoxControl.Watermark = CoreTools.Translate("Change install location");
        BrowseLocationButtonControl.Content = CoreTools.Translate("Select");
        ResetLocationButtonControl.Content = CoreTools.Translate("Reset");
        BrowseLocationButtonControl.Click += BrowseLocationButton_OnClick;
        ResetLocationButtonControl.Click += (_, _) => CustomLocationTextBoxControl.Text = string.Empty;

        ParametersTitleText.Text = CoreTools.Translate("Custom command-line arguments:");
        CustomParamsDescText.Text = CoreTools.Translate("Custom command-line arguments can change the way in which programs are installed, upgraded or uninstalled, in a way UniGetUI cannot control. Using custom command-lines can break packages. Proceed with caution.");
        CustomParams1TextBoxControl.Watermark = CoreTools.Translate("Custom arguments:");
        CustomParams2TextBoxControl.Watermark = CoreTools.Translate("Custom arguments:");
        CustomParams3TextBoxControl.Watermark = CoreTools.Translate("Custom arguments:");
        InstallParamsLabelText.Text = CoreTools.Translate("Custom install arguments:");
        UpdateParamsLabelText.Text = CoreTools.Translate("Custom update arguments:");
        UninstallParamsLabelText.Text = CoreTools.Translate("Custom uninstall arguments:");

        PrePostTitleText.Text = CoreTools.Translate("Pre-install command:");
        PrePostCommandsDescText.Text = CoreTools.Translate("Pre and post install commands will be run before and after a package gets installed, upgraded or uninstalled. Be aware that they may break things unless used carefully");
        PreInstallCommandBoxControl.Watermark = CoreTools.Translate("Pre-install command:");
        PostInstallCommandBoxControl.Watermark = CoreTools.Translate("Post-install command:");
        PreUpdateCommandBoxControl.Watermark = CoreTools.Translate("Pre-update command:");
        PostUpdateCommandBoxControl.Watermark = CoreTools.Translate("Post-update command:");
        PreUninstallCommandBoxControl.Watermark = CoreTools.Translate("Pre-uninstall command:");
        PostUninstallCommandBoxControl.Watermark = CoreTools.Translate("Post-uninstall command:");
        ToolTip.SetTip(AbortInstallCheckBoxControl, CoreTools.Translate("Abort install if pre-install command fails"));
        ToolTip.SetTip(AbortUpdateCheckBoxControl, CoreTools.Translate("Abort update if pre-update command fails"));
        ToolTip.SetTip(AbortUninstallCheckBoxControl, CoreTools.Translate("Abort uninstall if pre-uninstall command fails"));
        PreInstallLabelText.Text = CoreTools.Translate("Pre-install command:");
        PostInstallLabelText.Text = CoreTools.Translate("Post-install command:");
        PreUpdateLabelText.Text = CoreTools.Translate("Pre-update command:");
        PostUpdateLabelText.Text = CoreTools.Translate("Post-update command:");
        PreUninstallLabelText.Text = CoreTools.Translate("Pre-uninstall command:");
        PostUninstallLabelText.Text = CoreTools.Translate("Post-uninstall command:");
        AbortInstallCheckBoxControl.Content = CoreTools.Translate("Abort install if pre-install command fails");
        AbortUpdateCheckBoxControl.Content = CoreTools.Translate("Abort update if pre-update command fails");
        AbortUninstallCheckBoxControl.Content = CoreTools.Translate("Abort uninstall if pre-uninstall command fails");
    }

    private void PopulateStaticControls()
    {
        var caps = _package.Manager.Capabilities;

        RunAsAdminCheckBoxControl.IsEnabled = caps.CanRunAsAdmin;
        InteractiveCheckBoxControl.IsEnabled = caps.CanRunInteractively;
        HashCheckBoxControl.IsEnabled = caps.CanSkipIntegrityChecks;
        UninstallPreviousCheckBoxControl.IsVisible = caps.CanUninstallPreviousVersionsAfterUpdate;

        VersionComboBoxControl.Items.Add(CoreTools.Translate("Latest"));
        VersionComboBoxControl.SelectedIndex = 0;
        if (caps.SupportsPreRelease)
        {
            VersionComboBoxControl.Items.Add(CoreTools.Translate("PreRelease"));
        }
        VersionComboBoxControl.IsEnabled = caps.SupportsCustomVersions || caps.SupportsPreRelease;

        ArchitectureComboBoxControl.Items.Add(CoreTools.Translate("Default"));
        ArchitectureComboBoxControl.SelectedIndex = 0;
        if (caps.SupportsCustomArchitectures)
        {
            foreach (var arch in caps.SupportedCustomArchitectures)
            {
                ArchitectureComboBoxControl.Items.Add(arch);
            }
        }
        ArchitectureComboBoxControl.IsEnabled = caps.SupportsCustomArchitectures;

        ScopeComboBoxControl.Items.Add(CoreTools.Translate("Default"));
        ScopeComboBoxControl.SelectedIndex = 0;
        if (caps.SupportsCustomScopes)
        {
            ScopeComboBoxControl.Items.Add(CoreTools.Translate("User | Local"));
            ScopeComboBoxControl.Items.Add(CoreTools.Translate("Machine | Global"));
        }
        ScopeComboBoxControl.IsEnabled = caps.SupportsCustomScopes;

        CustomLocationCardControl.IsVisible = caps.SupportsCustomLocations;
        CustomParametersCardControl.IsVisible = SecureSettings.Get(SecureSettings.K.AllowCLIArguments);
        PrePostCommandsCardControl.IsVisible = SecureSettings.Get(SecureSettings.K.AllowPrePostOpCommand);
        KillProcessesThatWontDieCheckBoxControl.IsChecked = Settings.Get(Settings.K.KillProcessesThatRefuseToDie);
    }

    private async Task LoadOptionsAsync()
    {
        _options = await InstallOptionsFactory.LoadForPackageAsync(_package);
        ApplyOptionsToUi(_options);
        IgnoreUpdatesCheckBoxControl.IsChecked = await _package.HasUpdatesIgnoredAsync();

        if (_package.Manager.Capabilities.SupportsCustomVersions)
        {
            VersionLoadingBarControl.IsVisible = true;
            VersionComboBoxControl.IsEnabled = false;
            var versions = await Task.Run(() => _package.Manager.DetailsHelper.GetVersions(_package));
            foreach (var version in versions)
            {
                VersionComboBoxControl.Items.Add(version);
            }
            VersionLoadingBarControl.IsVisible = false;
            VersionComboBoxControl.IsEnabled = true;
        }
    }

    private void ApplyOptionsToUi(InstallOptions options)
    {
        RunAsAdminCheckBoxControl.IsChecked = options.RunAsAdministrator;
        InteractiveCheckBoxControl.IsChecked = options.InteractiveInstallation;
        HashCheckBoxControl.IsChecked = options.SkipHashCheck;
        AutoUpdateCheckBoxControl.IsChecked = options.AutoUpdatePackage;
        SkipMinorUpdatesCheckBoxControl.IsChecked = options.SkipMinorUpdates;
        UninstallPreviousCheckBoxControl.IsChecked = options.UninstallPreviousVersionsOnUpdate;
        KillProcessesTextBoxControl.Text = string.Join(", ", options.KillBeforeOperation);

        if (options.PreRelease && _package.Manager.Capabilities.SupportsPreRelease)
        {
            VersionComboBoxControl.SelectedItem = CoreTools.Translate("PreRelease");
        }
        else if (!string.IsNullOrWhiteSpace(options.Version))
        {
            var versionMatch = VersionComboBoxControl.Items.Cast<object?>()
                .FirstOrDefault(item => item?.ToString() == options.Version);
            if (versionMatch is not null)
            {
                VersionComboBoxControl.SelectedItem = versionMatch;
            }
        }

        if (!string.IsNullOrWhiteSpace(options.Architecture))
        {
            var architectureMatch = ArchitectureComboBoxControl.Items.Cast<object?>()
                .FirstOrDefault(item => item?.ToString() == options.Architecture);
            if (architectureMatch is not null)
            {
                ArchitectureComboBoxControl.SelectedItem = architectureMatch;
            }
        }

        if (!string.IsNullOrWhiteSpace(options.InstallationScope))
        {
            string? displayScope = options.InstallationScope == PackageScope.User
                ? CoreTools.Translate("User | Local")
                : options.InstallationScope == PackageScope.Machine
                    ? CoreTools.Translate("Machine | Global")
                    : null;
            if (displayScope is not null)
            {
                var scopeMatch = ScopeComboBoxControl.Items.Cast<object?>()
                    .FirstOrDefault(item => item?.ToString() == displayScope);
                if (scopeMatch is not null)
                {
                    ScopeComboBoxControl.SelectedItem = scopeMatch;
                }
            }
        }

        CustomLocationTextBoxControl.Text = options.CustomInstallLocation;
        CustomParams1TextBoxControl.Text = string.Join(' ', options.CustomParameters_Install);
        CustomParams2TextBoxControl.Text = string.Join(' ', options.CustomParameters_Update);
        CustomParams3TextBoxControl.Text = string.Join(' ', options.CustomParameters_Uninstall);
        PreInstallCommandBoxControl.Text = options.PreInstallCommand;
        PostInstallCommandBoxControl.Text = options.PostInstallCommand;
        AbortInstallCheckBoxControl.IsChecked = options.AbortOnPreInstallFail;
        PreUpdateCommandBoxControl.Text = options.PreUpdateCommand;
        PostUpdateCommandBoxControl.Text = options.PostUpdateCommand;
        AbortUpdateCheckBoxControl.IsChecked = options.AbortOnPreUpdateFail;
        PreUninstallCommandBoxControl.Text = options.PreUninstallCommand;
        PostUninstallCommandBoxControl.Text = options.PostUninstallCommand;
        AbortUninstallCheckBoxControl.IsChecked = options.AbortOnPreUninstallFail;
    }

    private InstallOptions ReadOptionsFromUi()
    {
        var options = new InstallOptions
        {
            RunAsAdministrator = RunAsAdminCheckBoxControl.IsChecked ?? false,
            InteractiveInstallation = InteractiveCheckBoxControl.IsChecked ?? false,
            SkipHashCheck = HashCheckBoxControl.IsChecked ?? false,
            AutoUpdatePackage = AutoUpdateCheckBoxControl.IsChecked ?? false,
            UninstallPreviousVersionsOnUpdate = UninstallPreviousCheckBoxControl.IsChecked ?? false,
            CustomInstallLocation = CustomLocationTextBoxControl.Text?.Trim() ?? string.Empty,
            PreInstallCommand = PreInstallCommandBoxControl.Text?.Trim() ?? string.Empty,
            PostInstallCommand = PostInstallCommandBoxControl.Text?.Trim() ?? string.Empty,
            AbortOnPreInstallFail = AbortInstallCheckBoxControl.IsChecked ?? true,
            PreUpdateCommand = PreUpdateCommandBoxControl.Text?.Trim() ?? string.Empty,
            PostUpdateCommand = PostUpdateCommandBoxControl.Text?.Trim() ?? string.Empty,
            AbortOnPreUpdateFail = AbortUpdateCheckBoxControl.IsChecked ?? true,
            PreUninstallCommand = PreUninstallCommandBoxControl.Text?.Trim() ?? string.Empty,
            PostUninstallCommand = PostUninstallCommandBoxControl.Text?.Trim() ?? string.Empty,
            AbortOnPreUninstallFail = AbortUninstallCheckBoxControl.IsChecked ?? true,
            SkipMinorUpdates = SkipMinorUpdatesCheckBoxControl.IsChecked ?? false,
            OverridesNextLevelOpts = true,
        };

        string versionSelection = VersionComboBoxControl.SelectedItem?.ToString() ?? string.Empty;
        if (versionSelection == CoreTools.Translate("PreRelease"))
        {
            options.PreRelease = true;
            options.Version = string.Empty;
        }
        else if (versionSelection == CoreTools.Translate("Latest") || string.IsNullOrWhiteSpace(versionSelection))
        {
            options.PreRelease = false;
            options.Version = string.Empty;
        }
        else
        {
            options.PreRelease = false;
            options.Version = versionSelection;
        }

        string architectureSelection = ArchitectureComboBoxControl.SelectedItem?.ToString() ?? string.Empty;
        options.Architecture = Architecture.ValidValues.Contains(architectureSelection)
            ? architectureSelection
            : string.Empty;

        string scopeSelection = ScopeComboBoxControl.SelectedItem?.ToString() ?? string.Empty;
        if (scopeSelection == CoreTools.Translate("User | Local"))
        {
            options.InstallationScope = PackageScope.User;
        }
        else if (scopeSelection == CoreTools.Translate("Machine | Global"))
        {
            options.InstallationScope = PackageScope.Machine;
        }
        else
        {
            options.InstallationScope = string.Empty;
        }

        options.CustomParameters_Install = SplitParams(CustomParams1TextBoxControl.Text);
        options.CustomParameters_Update = SplitParams(CustomParams2TextBoxControl.Text);
        options.CustomParameters_Uninstall = SplitParams(CustomParams3TextBoxControl.Text);

        options.KillBeforeOperation.Clear();
        var killNames = (KillProcessesTextBoxControl.Text ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var name in killNames)
        {
            if (!string.IsNullOrEmpty(name))
            {
                options.KillBeforeOperation.Add(name);
            }
        }

        return options;
    }

    private static List<string> SplitParams(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return [.. text.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    private async void BrowseLocationButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return;
        }

        var options = new FolderPickerOpenOptions
        {
            Title = CoreTools.Translate("Select a folder"),
            AllowMultiple = false,
        };

        if (!string.IsNullOrWhiteSpace(CustomLocationTextBoxControl.Text))
        {
            try
            {
                var current = await topLevel.StorageProvider.TryGetFolderFromPathAsync(
                    new Uri(CustomLocationTextBoxControl.Text.Trim()));
                if (current is not null)
                {
                    options.SuggestedStartLocation = current;
                }
            }
            catch
            {
                // ignore invalid path
            }
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
        if (folders.Count > 0)
        {
            CustomLocationTextBoxControl.Text = folders[0].TryGetLocalPath() ?? string.Empty;
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
