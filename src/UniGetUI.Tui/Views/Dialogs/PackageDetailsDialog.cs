using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Theme;

namespace UniGetUI.Tui.Views.Dialogs;

/// <summary>
/// The package details dialog (desktop <c>PackageDetailsWindow</c>): header, role-aware version text,
/// every <see cref="IPackageDetails"/> field once loaded, and the main action with its variants.
/// Closes with true when the caller should run the page's main action.
/// </summary>
internal sealed class PackageDetailsDialog : TuiDialog
{
    private readonly IPackage _package;
    private readonly OperationType _role;
    private readonly StackPanel _content = new() { Spacing = 0 };
    private readonly ScrollViewer _scroll;

    private PackageDetailsDialog(IPackage package, OperationType role)
        : base(CoreTools.Translate("Package details") + ": " + package.Name)
    {
        _package = package;
        _role = role;
        SetFrameWidth(100);
        SetFrameHeight(32);
        _scroll = new ScrollViewer { Content = _content, Focusable = true };
        Body.Content = _scroll;

        var caps = package.Manager.Capabilities;
        bool real = !package.Source.IsVirtualManager && package is not InvalidImportedPackage;
        AddButton(MainActionLabel(), () => Close(true)).IsEnabled = real && role != OperationType.None || package is ImportedPackage;
        if (role == OperationType.Install || role == OperationType.None)
        {
            AddVariant(CoreTools.Translate("As administrator"), caps.CanRunAsAdmin, () => TuiPackageActions.InstallAsync([package], elevated: true));
            AddVariant(CoreTools.Translate("Interactive"), caps.CanRunInteractively, () => TuiPackageActions.InstallAsync([package], interactive: true));
            AddVariant(CoreTools.Translate("Skip hash"), caps.CanSkipIntegrityChecks, () => TuiPackageActions.InstallAsync([package], skipHash: true));
        }
        else if (role == OperationType.Update)
        {
            AddVariant(CoreTools.Translate("As administrator"), caps.CanRunAsAdmin, () => TuiPackageActions.UpdateAsync([package], elevated: true));
            AddVariant(CoreTools.Translate("Interactive"), caps.CanRunInteractively, () => TuiPackageActions.UpdateAsync([package], interactive: true));
            AddVariant(CoreTools.Translate("Skip hash"), caps.CanSkipIntegrityChecks, () => TuiPackageActions.UpdateAsync([package], skipHash: true));
        }
        else if (role == OperationType.Uninstall)
        {
            AddVariant(CoreTools.Translate("As administrator"), caps.CanRunAsAdmin, () => TuiPackageActions.UninstallAsync([package], elevated: true));
            AddVariant(CoreTools.Translate("Interactive"), caps.CanRunInteractively, () => TuiPackageActions.UninstallAsync([package], interactive: true));
            AddVariant(CoreTools.Translate("Remove data"), caps.CanRemoveDataOnUninstall, () => TuiPackageActions.UninstallAsync([package], removeData: true));
        }

        AddButton(CoreTools.Translate("Options"), () => _ = OpenOptionsAsync()).IsEnabled = real;
        AddButton(CoreTools.Translate("Download"), () => _ = DownloadAsync()).IsEnabled = real && caps.CanDownloadInstaller;
        AddButton(CoreTools.Translate("Close"), () => Close(false));
        _ = LoadAsync();
    }

    public static async Task<bool> ShowAsync(IPackage package, OperationType role)
        => await TuiModal.ShowAsync(new PackageDetailsDialog(package, role)) is true;

    public override void FocusInitial() => _scroll.Focus();

    private string MainActionLabel() => _role switch
    {
        OperationType.Update => CoreTools.Translate("Update to {0}", _package.NewVersionString),
        OperationType.Uninstall => CoreTools.Translate("Uninstall"),
        _ => CoreTools.Translate("Install"),
    };

    private void AddVariant(string label, bool enabled, Func<Task<int>> run)
    {
        AddButton(label, () =>
        {
            _ = run();
            Close(false);
        }).IsEnabled = enabled && !_package.Source.IsVirtualManager;
    }

    private async Task OpenOptionsAsync()
    {
        bool proceed = await InstallOptionsDialog.ShowForPackageAsync(_package, _role == OperationType.None ? OperationType.Install : _role);
        if (proceed) Close(true);
    }

    private async Task DownloadAsync()
    {
        string? folder = await TuiPrompts.AskTextAsync(CoreTools.Translate("Download installer"),
            CoreTools.Translate("Download the installer(s) to this folder:"), TuiPackageActions.DefaultDownloadDirectory());
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        TuiPackageActions.Download([_package], folder);
    }

    private async Task LoadAsync()
    {
        Render(loaded: false);
        try
        {
            await _package.Details.Load();
        }
        catch (Exception ex)
        {
            UniGetUI.Core.Logging.Logger.Warn(ex);
        }

        Render(loaded: true);
    }

    private string VersionText()
    {
        return _role switch
        {
            OperationType.Update => $"{_package.VersionString} -> {_package.NewVersionString}",
            OperationType.Install when _package.GetInstalledPackages() is { Count: > 0 } installed
                => $"{_package.VersionString}  ({CoreTools.Translate("Installed")}: {string.Join(", ", installed.Select(p => p.VersionString))})",
            _ => _package.VersionString,
        };
    }

    private void Render(bool loaded)
    {
        _content.Children.Clear();
        _content.Children.Add(new TextBlock { Text = _package.Name, FontWeight = FontWeight.Bold, Foreground = TuiPalette.Brand });
        Field(CoreTools.Translate("Package ID"), _package.Id);
        Field(CoreTools.Translate("Version"), VersionText());
        Field(CoreTools.Translate("Source"), _package.Source.AsString_DisplayName);
        Field(CoreTools.Translate("Package Manager"), _package.Manager.DisplayName);
        if (_package.GetUpgradablePackage() is { } up && _role != OperationType.Update)
            Field(CoreTools.Translate("Update available"), up.NewVersionString);

        if (!loaded)
        {
            _content.Children.Add(new TextBlock { Text = CoreTools.Translate("Loading…"), Margin = new Thickness(0, 1, 0, 0) });
            return;
        }

        IPackageDetails d = _package.Details;
        _content.Children.Add(new TextBlock { Text = "", });
        Field(CoreTools.Translate("Description"), d.Description, wrap: true);
        Field(CoreTools.Translate("Publisher"), d.Publisher);
        Field(CoreTools.Translate("Author"), d.Author);
        Field(CoreTools.Translate("Homepage"), d.HomepageUrl?.ToString());
        Field(CoreTools.Translate("License"), d.License is null ? null : d.LicenseUrl is null ? d.License : $"{d.License} ({d.LicenseUrl})");
        Field(CoreTools.Translate("Manifest"), d.ManifestUrl?.ToString());
        Field(_package.Manager.Name == "Chocolatey" ? CoreTools.Translate("Installer SHA512") : CoreTools.Translate("Installer SHA256"), d.InstallerHash);
        Field(CoreTools.Translate("Installer type"), d.InstallerType);
        Field(CoreTools.Translate("Installer URL"), d.InstallerUrl?.ToString());
        Field(CoreTools.Translate("Download size"), d.InstallerSize > 0 ? CoreTools.FormatAsSize(d.InstallerSize) : null);
        Field(CoreTools.Translate("Last updated:"), d.UpdateDate);
        Field(CoreTools.Translate("Release notes"), d.ReleaseNotes, wrap: true);
        Field(CoreTools.Translate("Release notes URL"), d.ReleaseNotesUrl?.ToString());
        Field(CoreTools.Translate("Tags"), d.Tags.Length > 0 ? string.Join(", ", d.Tags) : null);
        if (_package.Manager.Capabilities.CanListDependencies)
        {
            Field(CoreTools.Translate("Dependencies"), d.Dependencies.Count == 0
                ? CoreTools.Translate("This package has no dependencies")
                : string.Join(", ", d.Dependencies.Select(x => $"{x.Name} {x.Version}{(x.Mandatory ? "" : " (" + CoreTools.Translate("optional") + ")")}")),
                wrap: true);
        }
    }

    private void Field(string label, string? value, bool wrap = false)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*") };
        var l = new TextBlock { Text = label, Foreground = TuiPalette.Brand };
        var v = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(value) ? CoreTools.Translate("Not available") : value,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        if (string.IsNullOrWhiteSpace(value)) v.Foreground = TuiPalette.TextDim;
        Grid.SetColumn(v, 1);
        grid.Children.Add(l);
        grid.Children.Add(v);
        _content.Children.Add(grid);
    }
}
