using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Telemetry;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Views;

public partial class PackageDetailsWindow : Window
{
    private const double WideThreshold = 950;
    private const string ContributeUrl = "https://github.com/Devolutions/UniGetUI";

    private enum LayoutMode { Unset, Normal, Wide }
    private LayoutMode _layoutMode = LayoutMode.Unset;

    /// <summary>True when the user confirmed the main action (install/update/uninstall) without extras.</summary>
    public bool ShouldProceedWithOperation { get; private set; }

    private readonly PackageDetailsViewModel _vm;
    private InstallOptionsViewModel? _installVm;
    private InstallOptions? _installOpts;

    public PackageDetailsWindow(IPackage package, OperationType operation)
    {
        _vm = new PackageDetailsViewModel(package, operation);
        DataContext = _vm;
        InitializeComponent();
        UniGetUI.Avalonia.Infrastructure.MicaWindowHelper.Apply(this);

        // Honor the OS "reduce motion" preference: drop the screenshot slide animation.
        if (MotionPreference.ReducedMotion)
            ScreenshotsCarousel.PageTransition = null;

        _vm.CloseRequested += (_, _) => Close();
        _vm.DetailsLoaded += (_, _) =>
        {
            BuildBasicInfoInlines();
            BuildDetailsInlines();
        };
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Screenshots.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(UpdatePips);

        MainActionButton.Click += (_, _) => OnMainAction();
        ActionVariantsButton.Flyout = BuildActionFlyout();
        InstallOptionsSaveButton.Click += (_, _) => _ = SaveInstallOptionsAsync();
        ContributeButton.Click += (_, _) => OpenUrl(ContributeUrl);
        PrevScreenshotButton.Click += (_, _) =>
        {
            if (_vm.SelectedScreenshotIndex > 0)
                _vm.SelectedScreenshotIndex--;
        };
        NextScreenshotButton.Click += (_, _) =>
        {
            if (_vm.SelectedScreenshotIndex < _vm.ScreenshotCount - 1)
                _vm.SelectedScreenshotIndex++;
        };
        ScreenshotPips.AddHandler(Button.ClickEvent, OnPipClicked);

        SizeChanged += (_, _) => ApplyLayoutForCurrentSize();

        // Seed inline blocks with loading placeholders.
        BuildBasicInfoInlines();
        BuildDetailsInlines();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyLayoutForCurrentSize();
        Dispatcher.UIThread.Post(() => MainActionButton.Focus(), DispatcherPriority.Background);
        _ = _vm.LoadDetailsAsync();
        TelemetryHandler.PackageDetails(_vm.Package, _vm.OperationRole.ToString());
        _ = InitInstallOptionsAsync();
    }

    private async Task InitInstallOptionsAsync()
    {
        _installOpts = await InstallOptionsFactory.LoadForPackageAsync(_vm.Package);
        _installVm = new InstallOptionsViewModel(_vm.Package, _vm.OperationRole, _installOpts);
        var embed = new InstallOptionsControl { DataContext = _installVm };
        InstallOptionsHolder.Content = embed;
    }

    private async Task SaveInstallOptionsAsync()
    {
        if (_installVm is null || _installOpts is null) return;
        _installVm.ApplyChanges();
        await InstallOptionsFactory.SaveForPackageAsync(_installOpts, _vm.Package);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PackageDetailsViewModel.SelectedScreenshotIndex)
            || e.PropertyName == nameof(PackageDetailsViewModel.ScreenshotCount))
            Dispatcher.UIThread.Post(UpdatePips);
    }

    private void OnPipClicked(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Control src) return;
        // Walk up to find the pip Button container; ask the ItemsControl for its index.
        Control? cursor = src;
        while (cursor is not null && cursor.Parent is not ItemsControl)
            cursor = cursor.Parent as Control;
        if (cursor is null) return;
        int idx = ScreenshotPips.IndexFromContainer(cursor);
        if (idx >= 0 && idx < _vm.ScreenshotCount)
            _vm.SelectedScreenshotIndex = idx;
    }

    private void UpdatePips()
    {
        int active = _vm.SelectedScreenshotIndex;
        int i = 0;
        foreach (var container in ScreenshotPips.GetRealizedContainers())
        {
            // The pip template is <Button><Ellipse/></Button>; the realized container is the Button itself.
            if (container is Button btn && btn.Content is Ellipse ellipse)
                ellipse.Classes.Set("active", i == active);
            i++;
        }
    }

    // ── Responsive layout ────────────────────────────────────────────────────

    private void ApplyLayoutForCurrentSize()
    {
        var wide = Bounds.Width >= WideThreshold;
        var mode = wide ? LayoutMode.Wide : LayoutMode.Normal;
        if (mode == _layoutMode) return;
        _layoutMode = mode;

        if (mode == LayoutMode.Wide)
        {
            // Ensure two columns and the right-column panels live in RightPanel.
            EnsureChild(RightPanel, ScreenshotsPanel, 0);
            EnsureChild(RightPanel, DetailsPanel, 1);
            RightPanel.IsVisible = true;
            MainGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);

            ScreenshotsBorder.Height = _vm.HasScreenshots ? 320 : 150;
        }
        else
        {
            // Move screenshots + details into LeftPanel (after install options) and collapse the right column.
            EnsureChild(LeftPanel, ScreenshotsPanel, LeftPanel.Children.Count);
            EnsureChild(LeftPanel, DetailsPanel, LeftPanel.Children.Count);
            RightPanel.IsVisible = false;
            MainGrid.ColumnDefinitions[1].Width = new GridLength(0);

            ScreenshotsBorder.Height = _vm.HasScreenshots ? 225 : 130;
        }
    }

    /// <summary>Move <paramref name="child"/> to <paramref name="target"/> at the given index, removing from its old parent first.</summary>
    private static void EnsureChild(Panel target, Control child, int index)
    {
        if (child.Parent is Panel current && current != target)
            current.Children.Remove(child);
        if (!target.Children.Contains(child))
            target.Children.Insert(Math.Min(index, target.Children.Count), child);
    }

    // ── Per-row layout builders (inline flow like WinUI's RichTextBlock) ─────

    private void BuildBasicInfoInlines()
    {
        BasicInfoPanel.Children.Clear();

        if (!string.IsNullOrWhiteSpace(_vm.Description))
        {
            BasicInfoPanel.Children.Add(new SelectableTextBlock
            {
                Text = _vm.Description,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
        }

        AddInlineRow(BasicInfoPanel, _vm.LabelHomepage, _vm.HomepageUrl);
        AddInlineRow(BasicInfoPanel, _vm.LabelPublisher, _vm.Publisher);
        AddInlineRow(BasicInfoPanel, _vm.LabelAuthor, _vm.Author);
        AddLicenseRow(BasicInfoPanel);
        AddInlineRow(BasicInfoPanel, _vm.LabelUpdateDate, _vm.UpdateDate);
        AddInlineRow(BasicInfoPanel, _vm.LabelSource, _vm.SourceDisplay);
    }

    private void BuildDetailsInlines()
    {
        DetailsPanel.Children.Clear();

        AddInlineRow(DetailsPanel, _vm.LabelPackageId, _vm.PackageId);
        AddInlineRow(DetailsPanel, _vm.LabelManifest, _vm.ManifestUrl);
        AddInlineRow(DetailsPanel, _vm.LabelVersion, _vm.VersionDisplay);

        AddSpacer(DetailsPanel);

        AddInlineRow(DetailsPanel, _vm.LabelInstallerType, _vm.InstallerType);
        AddInlineRow(DetailsPanel, _vm.LabelInstallerUrl, _vm.InstallerUrl);
        AddInlineRow(DetailsPanel, _vm.InstallerHashLabel.TrimEnd(':'), _vm.InstallerHash);

        AddDownloadRow(DetailsPanel);

        AddSpacer(DetailsPanel);

        // Dependencies header + list
        DetailsPanel.Children.Add(new SelectableTextBlock
        {
            Text = _vm.LabelDependencies + ":",
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 4),
        });
        if (_vm.HasDependencyNote)
        {
            DetailsPanel.Children.Add(new SelectableTextBlock
            {
                Text = "  " + _vm.DependencyNote,
                Foreground = NotAvailableBrush,
                Margin = new Thickness(0, 0, 0, 4),
            });
        }
        else
        {
            foreach (var dep in _vm.Dependencies)
            {
                DetailsPanel.Children.Add(new SelectableTextBlock
                {
                    Text = dep.DisplayText,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }

        AddSpacer(DetailsPanel);

        // Release notes
        DetailsPanel.Children.Add(new SelectableTextBlock
        {
            Text = _vm.LabelReleaseNotes + ":",
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 4),
        });
        DetailsPanel.Children.Add(new SelectableTextBlock
        {
            Text = _vm.ReleaseNotes,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        AddInlineRow(DetailsPanel, _vm.LabelReleaseNotesUrl, _vm.ReleaseNotesUrl);
    }

    private static readonly IBrush NotAvailableBrush =
        new SolidColorBrush(Color.FromArgb(255, 127, 127, 127));

    private static void AddSpacer(StackPanel host) =>
        host.Children.Add(new Border { Height = 10 });

    /// <summary>
    /// Builds a single wrap-able row with "<bold>Label:</bold> value" all on one line,
    /// matching the WinUI RichTextBlock paragraph layout.
    /// </summary>
    private void AddInlineRow(StackPanel host, string label, string value)
    {
        var tb = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        var inlines = tb.Inlines ??= new InlineCollection();
        inlines.Add(new Run(label + ": ") { FontWeight = FontWeight.Bold });
        if (string.IsNullOrWhiteSpace(value) || value == _vm.LabelNotAvailable)
            inlines.Add(new Run(_vm.LabelNotAvailable)
            {
                Foreground = NotAvailableBrush,
                FontStyle = FontStyle.Italic,
            });
        else
            inlines.Add(new Run(value));
        host.Children.Add(tb);
    }

    private void AddInlineRow(StackPanel host, string label, Uri? url)
    {
        if (url is null)
        {
            var tb = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
            var inlines = tb.Inlines ??= new InlineCollection();
            inlines.Add(new Run(label + ": ") { FontWeight = FontWeight.Bold });
            inlines.Add(new Run(_vm.LabelNotAvailable)
            {
                Foreground = NotAvailableBrush,
                FontStyle = FontStyle.Italic,
            });
            host.Children.Add(tb);
            return;
        }

        host.Children.Add(BuildLinkRow(BoldLabel(label + ": "), url.ToString()));
    }

    private void AddLicenseRow(StackPanel host)
    {
        bool hasName = !string.IsNullOrEmpty(_vm.LicenseName);
        bool hasUrl = _vm.LicenseUrl is not null;

        if (!hasUrl)
        {
            // No link: render as a plain selectable line with the usual cursor.
            var tb = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
            var inlines = tb.Inlines ??= new InlineCollection();
            inlines.Add(new Run(_vm.LabelLicense + ": ") { FontWeight = FontWeight.Bold });
            if (hasName)
                inlines.Add(new Run(_vm.LicenseName!));
            else
                inlines.Add(new Run(_vm.LabelNotAvailable)
                {
                    Foreground = NotAvailableBrush,
                    FontStyle = FontStyle.Italic,
                });
            host.Children.Add(tb);
            return;
        }

        var labelBlock = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var labelInlines = labelBlock.Inlines ??= new InlineCollection();
        labelInlines.Add(new Run(_vm.LabelLicense + ": ") { FontWeight = FontWeight.Bold });
        if (hasName)
            labelInlines.Add(new Run(_vm.LicenseName! + " "));

        host.Children.Add(BuildLinkRow(labelBlock, _vm.LicenseUrl!.ToString()));
    }

    private void AddDownloadRow(StackPanel host)
    {
        if (_vm.CanDownloadInstaller && !_vm.IsLoading)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var link = CreateLinkBlock(_vm.LabelDownloadInstaller, DownloadInstaller);
            link.FontWeight = FontWeight.SemiBold;
            panel.Children.Add(link);
            if (!string.IsNullOrEmpty(_vm.InstallerSize))
                panel.Children.Add(new SelectableTextBlock { Text = $" ({_vm.InstallerSize})" });
            host.Children.Add(panel);
        }
        else
        {
            host.Children.Add(new SelectableTextBlock
            {
                Text = _vm.LabelInstallerNotAvailable,
                Foreground = NotAvailableBrush,
                FontStyle = FontStyle.Italic,
            });
        }
    }

    private static SelectableTextBlock BoldLabel(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        VerticalAlignment = VerticalAlignment.Top,
    };

    /// <summary>"Label: url" laid out so a long URL wraps under itself; only the URL is the link.</summary>
    private Grid BuildLinkRow(Control label, string url)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
            ],
        };
        Grid.SetColumn(label, 0);
        var link = CreateLinkBlock(url, () => OpenUrl(url));
        link.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(link, 1);
        grid.Children.Add(label);
        grid.Children.Add(link);
        return grid;
    }

    /// <summary>A selectable, copyable hyperlink. The hand cursor and activation cover only this text.</summary>
    private SelectableTextBlock CreateLinkBlock(string text, Action activate)
    {
        var link = new SelectableTextBlock
        {
            Text = text,
            Foreground = LinkBrush,
            TextDecorations = TextDecorations.Underline,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        AttachLinkActivation(link, activate);
        return link;
    }

    /// <summary>Activate on a clean click, but let a press-and-drag run the text selection instead.</summary>
    private static void AttachLinkActivation(Control link, Action activate)
    {
        Point press = default;
        bool tracking = false;

        link.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(link).Properties.IsLeftButtonPressed)
            {
                tracking = true;
                press = e.GetPosition(link);
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        link.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (!tracking) return;
            tracking = false;
            var moved = e.GetPosition(link) - press;
            if (Math.Abs(moved.X) <= 4 && Math.Abs(moved.Y) <= 4)
                activate();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private IBrush LinkBrush =>
        this.TryFindResource("HyperlinkForeground", ActualThemeVariant, out var res) && res is IBrush b
            ? b
            : new SolidColorBrush(Color.FromArgb(255, 0, 120, 212));

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to open URL: {url}");
            Logger.Error(ex);
        }
    }

    private void DownloadInstaller()
    {
        if (!_vm.Package.Manager.Capabilities.CanDownloadInstaller) return;
        Close();
        // Hook into the platform's download path if/when wired up. For now, fall back to opening
        // the installer URL so the user can still grab the file.
        if (_vm.InstallerUrl is not null)
            OpenUrl(_vm.InstallerUrl.ToString());
    }

    // ── Action flyout ────────────────────────────────────────────────────────

    private MenuFlyout BuildActionFlyout()
    {
        var flyout = new MenuFlyout();
        var asAdmin = new MenuItem { Header = _vm.AsAdminLabel, IsEnabled = _vm.CanRunAsAdmin };
        var interactive = new MenuItem { Header = _vm.InteractiveLabel, IsEnabled = _vm.CanRunInteractively };
        var skipOrRemove = new MenuItem { Header = _vm.SkipHashOrRemoveDataLabel, IsEnabled = _vm.CanSkipHashOrRemoveData };

        var role = _vm.OperationRole;
        if (role is OperationType.Uninstall)
        {
            asAdmin.Click += (_, _) => _ = LaunchAndClose(role, elevated: true);
            interactive.Click += (_, _) => _ = LaunchAndClose(role, interactive: true);
            skipOrRemove.Click += (_, _) => _ = LaunchAndClose(role, remove_data: true);
        }
        else
        {
            asAdmin.Click += (_, _) => _ = LaunchAndClose(role, elevated: true);
            interactive.Click += (_, _) => _ = LaunchAndClose(role, interactive: true);
            skipOrRemove.Click += (_, _) => _ = LaunchAndClose(role, no_integrity: true);
        }

        flyout.Items.Add(asAdmin);
        flyout.Items.Add(interactive);
        flyout.Items.Add(skipOrRemove);
        return flyout;
    }

    private void OnMainAction()
    {
        ShouldProceedWithOperation = true;
        Close();
    }

    private async Task LaunchAndClose(
        OperationType role,
        bool? elevated = null,
        bool? interactive = null,
        bool? no_integrity = null,
        bool? remove_data = null)
    {
        Close();

        var pkg = _vm.Package;
        var opts = await InstallOptionsFactory.LoadApplicableAsync(
            pkg,
            elevated: elevated,
            interactive: interactive,
            no_integrity: no_integrity,
            remove_data: remove_data);

        AbstractOperation op = role switch
        {
            OperationType.Install => new InstallPackageOperation(pkg, opts),
            OperationType.Update => new UpdatePackageOperation(pkg, opts),
            OperationType.Uninstall => new UninstallPackageOperation(pkg, opts),
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

        switch (role)
        {
            case OperationType.Install:
                op.OperationSucceeded += (_, _) => TelemetryHandler.InstallPackage(pkg, TEL_OP_RESULT.SUCCESS, TEL_InstallReferral.DIRECT_SEARCH);
                op.OperationFailed += (_, _) => TelemetryHandler.InstallPackage(pkg, TEL_OP_RESULT.FAILED, TEL_InstallReferral.DIRECT_SEARCH);
                break;
            case OperationType.Update:
                op.OperationSucceeded += (_, _) => TelemetryHandler.UpdatePackage(pkg, TEL_OP_RESULT.SUCCESS);
                op.OperationFailed += (_, _) => TelemetryHandler.UpdatePackage(pkg, TEL_OP_RESULT.FAILED);
                break;
            case OperationType.Uninstall:
                op.OperationSucceeded += (_, _) => TelemetryHandler.UninstallPackage(pkg, TEL_OP_RESULT.SUCCESS);
                op.OperationFailed += (_, _) => TelemetryHandler.UninstallPackage(pkg, TEL_OP_RESULT.FAILED);
                break;
        }

        AvaloniaOperationRegistry.Add(op);
        _ = op.MainThread();
    }
}
