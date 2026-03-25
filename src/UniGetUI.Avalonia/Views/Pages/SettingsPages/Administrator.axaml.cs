using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class Administrator : UserControl, ISettingsPage
{
    private AdministratorViewModel VM => (AdministratorViewModel)DataContext!;

    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Administrator rights and other dangerous settings");

    public event EventHandler? RestartRequired;
    public event EventHandler<Type>? NavigationRequested { add { } remove { } }

    public void ShowRestartBanner(object? sender, EventArgs e) =>
        RestartRequired?.Invoke(this, e);

    public void RestartCache(object? sender, EventArgs e) =>
        _ = CoreTools.ResetUACForCurrentProcess();

    public Administrator()
    {
        DataContext = new AdministratorViewModel();
        InitializeComponent();

        // Populate the warning banner
        WarningTitleText.Text = CoreTools.Translate("Warning") + "!";
        WarningBodyLine1.Text =
            CoreTools.Translate("The following settings may pose a security risk, hence they are disabled by default.")
            + " "
            + CoreTools.Translate("Enable the settings below if and only if you fully understand what they do, and the implications they may have.");
        WarningBodyLine2.Text =
            CoreTools.Translate("The settings will list, in their descriptions, the potential security issues they may have.");

        // Admin rights section
        DoCacheAdminRightsForBatches.SettingName = Settings.K.DoCacheAdminRightsForBatches;
        DoCacheAdminRightsForBatches.Text = CoreTools.Translate("Ask for administrator privileges once for each batch of operations");
        DoCacheAdminRightsForBatches.StateChanged += RestartCache;

        DoCacheAdminRights.SettingName = Settings.K.DoCacheAdminRights;
        DoCacheAdminRights.Text = CoreTools.Translate("Ask only once for administrator privileges");
        DoCacheAdminRights.StateChanged += RestartCache;

        ProhibitElevator.SettingName = Settings.K.ProhibitElevation;
        ProhibitElevator.Text = CoreTools.Translate("Prohibit any kind of Elevation via UniGetUI Elevator or GSudo");
        ProhibitElevator.StateChanged += ShowRestartBanner;

        // Bind IsElevationEnabled to the ProhibitElevator toggle (inverted)
        bool initialElevated = !(ProhibitElevator._checkbox.IsChecked ?? false);
        VM.IsElevationEnabled = initialElevated;

        ProhibitElevator._checkbox.IsCheckedChanged += (_, _) =>
            VM.IsElevationEnabled = !(ProhibitElevator._checkbox.IsChecked ?? false);

        // CLI arguments section
        AllowCLIArguments.SettingName = SecureSettings.K.AllowCLIArguments;
        AllowCLIArguments.Text = CoreTools.Translate("Allow custom command-line arguments");
        AllowCLIArguments._warningBlock.Text = TwoLineWarning(
            "Custom command-line arguments can change the way in which programs are installed, upgraded or uninstalled, in a way UniGetUI cannot control. Using custom command-lines can break packages. Proceed with caution.");
        AllowCLIArguments._warningBlock.IsVisible = true;

        AllowPrePostInstallCommands.SettingName = SecureSettings.K.AllowPrePostOpCommand;
        AllowPrePostInstallCommands.Text = CoreTools.Translate("Ignore custom pre-install and post-install commands when importing packages from a bundle");
        AllowPrePostInstallCommands._warningBlock.Text = TwoLineWarning(
            "Pre and post install commands will be run before and after a package gets installed, upgraded or uninstalled. Be aware that they may break things unless used carefully");
        AllowPrePostInstallCommands._warningBlock.IsVisible = true;

        // Manager paths section
        AllowCustomManagerPaths.SettingName = SecureSettings.K.AllowCustomManagerPaths;
        AllowCustomManagerPaths.Text = CoreTools.Translate("Allow changing the paths for package manager executables");
        AllowCustomManagerPaths._warningBlock.Text = TwoLineWarning(
            "Turning this on enables changing the executable file used to interact with package managers. While this allows finer-grained customization of your install processes, it may also be dangerous");
        AllowCustomManagerPaths._warningBlock.IsVisible = true;
        AllowCustomManagerPaths.StateChanged += ShowRestartBanner;

        // Bundle import restrictions
        AllowImportingCLIArguments.SettingName = SecureSettings.K.AllowImportingCLIArguments;
        AllowImportingCLIArguments.Text = CoreTools.Translate("Allow importing custom command-line arguments when importing packages from a bundle");
        AllowImportingCLIArguments._warningBlock.Text = TwoLineWarning(
            "Malformed command-line arguments can break packages, or even allow a malicious actor to gain privileged execution. Therefore, importing custom command-line arguments is disabled by default");
        AllowImportingCLIArguments._warningBlock.IsVisible = true;

        AllowImportingPrePostInstallCommands.SettingName = SecureSettings.K.AllowImportPrePostOpCommands;
        AllowImportingPrePostInstallCommands.Text = CoreTools.Translate("Allow importing custom pre-install and post-install commands when importing packages from a bundle");
        AllowImportingPrePostInstallCommands._warningBlock.Text = TwoLineWarning(
            "Pre and post install commands can do very nasty things to your device, if designed to do so. It can be very dangerous to import the commands from a bundle, unless you trust the source of that package bundle");
        AllowImportingPrePostInstallCommands._warningBlock.IsVisible = true;

        // Bind import cards' enabled state directly to parent toggles (imperative, since
        // SecureCheckboxCard.IsEnabled overrides the base property and can't use compiled bindings)
        AllowImportingCLIArguments.IsEnabled = AllowCLIArguments._checkbox.IsChecked ?? false;
        AllowImportingPrePostInstallCommands.IsEnabled = AllowPrePostInstallCommands._checkbox.IsChecked ?? false;

        AllowCLIArguments._checkbox.IsCheckedChanged += (_, _) =>
            AllowImportingCLIArguments.IsEnabled = AllowCLIArguments._checkbox.IsChecked ?? false;

        AllowPrePostInstallCommands._checkbox.IsCheckedChanged += (_, _) =>
            AllowImportingPrePostInstallCommands.IsEnabled = AllowPrePostInstallCommands._checkbox.IsChecked ?? false;
    }

    // Translates the full key (correct translation) then splits at the first sentence
    // boundary so the warning always renders on two readable lines.
    // Handles both Latin (". ") and CJK ("。") sentence separators.
    private static string TwoLineWarning(string key)
    {
        var text = CoreTools.Translate(key);
        // Try Latin period+space first, then CJK ideographic full stop
        var idx = text.IndexOf(". ", StringComparison.Ordinal);
        if (idx >= 0)
            return text[..(idx + 1)] + "\n" + text[(idx + 2)..];
        idx = text.IndexOf('。');
        if (idx >= 0)
            return text[..(idx + 1)] + "\n" + text[(idx + 1)..];
        return text;
    }
}
