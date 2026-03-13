using System.Diagnostics;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Models;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class PackageDetailsWindow : Window
{
    private const string ScreenshotsInfoUrl = "https://marticliment.com/unigetui/help/icons-and-screenshots/#about-icons";

    private readonly IPackage _package;
    private readonly PackagePageMode _pageMode;
    private readonly InstallOptionsEditorView? _installOptionsEditorView;
    private Uri? _homepageUrl;
    private Uri? _releaseNotesUrl;
    private Uri? _manifestUrl;
    private IReadOnlyList<Uri> _screenshotUris = [];
    private int _selectedScreenshotIndex;
    private bool _isTwoColumnLayout;

    private TextBlock PackageVersionLabelText => GetControl<TextBlock>("PackageVersionLabelBlock");
    private TextBlock PackageManagerLabelText => GetControl<TextBlock>("PackageManagerLabelBlock");
    private TextBlock PackageNameText => GetControl<TextBlock>("PackageNameBlock");
    private TextBlock PackageIdText => GetControl<TextBlock>("PackageIdBlock");
    private TextBlock PackageVersionText => GetControl<TextBlock>("PackageVersionBlock");
    private TextBlock PackageManagerText => GetControl<TextBlock>("PackageManagerBlock");
    private Border LoadingCardControl => GetControl<Border>("LoadingCard");
    private TextBlock LoadingStateText => GetControl<TextBlock>("LoadingStateBlock");
    private Border ScreenshotsCardControl => GetControl<Border>("ScreenshotsCard");
    private TextBlock ScreenshotsTitleText => GetControl<TextBlock>("ScreenshotsTitleBlock");
    private TextBlock ScreenshotsPipsText => GetControl<TextBlock>("ScreenshotsPipsBlock");
    private ProgressBar ScreenshotsLoadingBarControl => GetControl<ProgressBar>("ScreenshotsLoadingBar");
    private Border ScreenshotsImageCardControl => GetControl<Border>("ScreenshotsImageCard");
    private Image ScreenshotImageControl => GetControl<Image>("ScreenshotImage");
    private Button PreviousScreenshotButtonControl => GetControl<Button>("PreviousScreenshotButton");
    private Button NextScreenshotButtonControl => GetControl<Button>("NextScreenshotButton");
    private Border ScreenshotsBannerCardControl => GetControl<Border>("ScreenshotsBannerCard");
    private TextBlock ScreenshotsBannerText => GetControl<TextBlock>("ScreenshotsBannerTextBlock");
    private Button ContributeScreenshotsButtonControl => GetControl<Button>("ContributeScreenshotsButton");
    private Border TagsCardControl => GetControl<Border>("TagsCard");
    private WrapPanel TagsPanelControl => GetControl<WrapPanel>("TagsPanel");
    private Border DescriptionCardControl => GetControl<Border>("DescriptionCard");
    private TextBlock DescriptionTitleText => GetControl<TextBlock>("DescriptionTitleBlock");
    private TextBlock DescriptionText => GetControl<TextBlock>("DescriptionBlock");
    private Border InfoCardControl => GetControl<Border>("InfoCard");
    private TextBlock InfoTitleText => GetControl<TextBlock>("InfoTitleBlock");
    private Grid AuthorRowControl => GetControl<Grid>("AuthorRow");
    private TextBlock AuthorLabelText => GetControl<TextBlock>("AuthorLabelBlock");
    private TextBlock AuthorValueText => GetControl<TextBlock>("AuthorValueBlock");
    private Grid PublisherRowControl => GetControl<Grid>("PublisherRow");
    private TextBlock PublisherLabelText => GetControl<TextBlock>("PublisherLabelBlock");
    private TextBlock PublisherValueText => GetControl<TextBlock>("PublisherValueBlock");
    private Grid LicenseRowControl => GetControl<Grid>("LicenseRow");
    private TextBlock LicenseLabelText => GetControl<TextBlock>("LicenseLabelBlock");
    private TextBlock LicenseValueText => GetControl<TextBlock>("LicenseValueBlock");
    private Grid UpdateDateRowControl => GetControl<Grid>("UpdateDateRow");
    private TextBlock UpdateDateLabelText => GetControl<TextBlock>("UpdateDateLabelBlock");
    private TextBlock UpdateDateValueText => GetControl<TextBlock>("UpdateDateValueBlock");
    private Grid InstallerTypeRowControl => GetControl<Grid>("InstallerTypeRow");
    private TextBlock InstallerTypeLabelText => GetControl<TextBlock>("InstallerTypeLabelBlock");
    private TextBlock InstallerTypeValueText => GetControl<TextBlock>("InstallerTypeValueBlock");
    private Border LinksCardControl => GetControl<Border>("LinksCard");
    private TextBlock LinksTitleText => GetControl<TextBlock>("LinksTitleBlock");
    private Button HomepageButtonControl => GetControl<Button>("HomepageButton");
    private Button ReleaseNotesUrlButtonControl => GetControl<Button>("ReleaseNotesUrlButton");
    private Button ManifestUrlButtonControl => GetControl<Button>("ManifestUrlButton");
    private Border ReleaseNotesCardControl => GetControl<Border>("ReleaseNotesCard");
    private TextBlock ReleaseNotesTitleText => GetControl<TextBlock>("ReleaseNotesTitleBlock");
    private TextBlock ReleaseNotesText => GetControl<TextBlock>("ReleaseNotesBlock");
    private Border InstallOptionsCardControl => GetControl<Border>("InstallOptionsCard");
    private Expander InstallOptionsExpanderControl => GetControl<Expander>("InstallOptionsExpander");
    private TextBlock InstallOptionsTitleText => GetControl<TextBlock>("InstallOptionsTitleBlock");
    private ContentControl InstallOptionsHostControl => GetControl<ContentControl>("InstallOptionsHost");
    private Button SaveInstallOptionsButtonControl => GetControl<Button>("SaveInstallOptionsButton");
    private Button ShareButtonControl => GetControl<Button>("ShareButton");
    private Button CloseButtonControl => GetControl<Button>("CloseButton");
    private Button ActionButtonControl => GetControl<Button>("ActionButton");
    private Button ActionDropdownButtonControl => GetControl<Button>("ActionDropdownButton");
    private Image PackageIconImageControl => GetControl<Image>("PackageIconImage");
    private TextBlock PackageIconGlyphControl => GetControl<TextBlock>("PackageIconGlyphBlock");
    private Border InstallerCardControl => GetControl<Border>("InstallerCard");
    private TextBlock InstallerTitleText => GetControl<TextBlock>("InstallerTitleBlock");
    private Grid InstallerHashRowControl => GetControl<Grid>("InstallerHashRow");
    private TextBlock InstallerHashLabelText => GetControl<TextBlock>("InstallerHashLabelBlock");
    private TextBlock InstallerHashValueText => GetControl<TextBlock>("InstallerHashValueBlock");
    private Grid InstallerUrlRowControl => GetControl<Grid>("InstallerUrlRow");
    private TextBlock InstallerUrlLabelText => GetControl<TextBlock>("InstallerUrlLabelBlock");
    private TextBlock InstallerUrlValueText => GetControl<TextBlock>("InstallerUrlValueBlock");
    private Grid InstallerSizeRowControl => GetControl<Grid>("InstallerSizeRow");
    private TextBlock InstallerSizeLabelText => GetControl<TextBlock>("InstallerSizeLabelBlock");
    private TextBlock InstallerSizeValueText => GetControl<TextBlock>("InstallerSizeValueBlock");
    private Button DownloadInstallerButtonControl => GetControl<Button>("DownloadInstallerButton");
    private Border DependenciesCardControl => GetControl<Border>("DependenciesCard");
    private TextBlock DependenciesTitleText => GetControl<TextBlock>("DependenciesTitleBlock");
    private StackPanel DependenciesPanelControl => GetControl<StackPanel>("DependenciesPanel");
    private Grid RootLayoutGridControl => GetControl<Grid>("RootLayoutGrid");
    private StackPanel PrimaryContentControl => GetControl<StackPanel>("PrimaryContent");
    private StackPanel SidePanelContentControl => GetControl<StackPanel>("SidePanelContent");

    public PackageDetailsWindow()
    {
        _package = null!;
        _pageMode = PackagePageMode.None;
        InitializeComponent();
        IsEnabled = false;
    }

    public PackageDetailsWindow(IPackage package, PackagePageMode pageMode)
    {
        _package = package;
        _pageMode = pageMode;
        if (pageMode != PackagePageMode.None)
        {
            _installOptionsEditorView = new InstallOptionsEditorView(package);
        }
        InitializeComponent();
        Opened += (_, _) => UpdateColumnsLayout(Bounds.Width);
        ApplyStaticContent(package);
        _ = LoadDetailsAsync();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateColumnsLayout(e.NewSize.Width);
    }

    private void UpdateColumnsLayout(double width)
    {
        bool wantTwoColumn = width >= 950;
        if (wantTwoColumn == _isTwoColumnLayout) return;
        _isTwoColumnLayout = wantTwoColumn;

        var rootGrid = RootLayoutGridControl;
        var primaryContent = PrimaryContentControl;
        var sideContent = SidePanelContentControl;
        var screenshotsCard = ScreenshotsCardControl;

        if (wantTwoColumn)
        {
            rootGrid.ColumnDefinitions[1].Width = new GridLength(380, GridUnitType.Pixel);
            rootGrid.ColumnSpacing = 12;
            if (primaryContent.Children.Contains(screenshotsCard))
            {
                primaryContent.Children.Remove(screenshotsCard);
                sideContent.Children.Insert(0, screenshotsCard);
            }
        }
        else
        {
            rootGrid.ColumnDefinitions[1].Width = new GridLength(0, GridUnitType.Pixel);
            rootGrid.ColumnSpacing = 0;
            if (sideContent.Children.Contains(screenshotsCard))
            {
                sideContent.Children.Remove(screenshotsCard);
                // Insert after header (index 0) — LoadingCard moves to index 1
                primaryContent.Children.Insert(Math.Min(1, primaryContent.Children.Count), screenshotsCard);
            }
        }
    }

    private void ApplyStaticContent(IPackage package)
    {
        Title = package.Name + " — " + CoreTools.Translate("Package details");

        PackageNameText.Text = package.Name;
        PackageIdText.Text = package.Id;
        PackageVersionText.Text = string.IsNullOrWhiteSpace(package.VersionString)
            ? CoreTools.Translate("Unknown")
            : package.VersionString;
        PackageManagerText.Text = package.Source.AsString_DisplayName;

        PackageVersionLabelText.Text = CoreTools.Translate("Version:");
        PackageManagerLabelText.Text = CoreTools.Translate("Package manager");

        if (_pageMode != PackagePageMode.None)
        {
            ActionButtonControl.Content = _pageMode switch
            {
                PackagePageMode.Discover => CoreTools.Translate("Install"),
                PackagePageMode.Updates => CoreTools.Translate("Update"),
                PackagePageMode.Installed => CoreTools.Translate("Uninstall"),
                _ => string.Empty,
            };
            ActionButtonControl.IsVisible = true;
            // ActionDropdownButton visibility is set by BuildActionFlyout() after capability check
        }

        LoadingStateText.Text = CoreTools.Translate("Loading...");
        ShareButtonControl.Content = CoreTools.Translate("Share this package");
        CloseButtonControl.Content = CoreTools.Translate("Close");

        DescriptionTitleText.Text = CoreTools.Translate("Description:");
        ScreenshotsTitleText.Text = CoreTools.Translate("Package details");
        ScreenshotsBannerText.Text = CoreTools.Translate("The icons and screenshots are maintained by users like you!");
        ContributeScreenshotsButtonControl.Content = CoreTools.Translate("Become a contributor");
        InfoTitleText.Text = CoreTools.Translate("Package details");
        LinksTitleText.Text = CoreTools.Translate("Useful links");
        ReleaseNotesTitleText.Text = CoreTools.Translate("Release notes");
        InstallOptionsTitleText.Text = CoreTools.Translate("Installation options");
        SaveInstallOptionsButtonControl.Content = CoreTools.Translate("Save");
        SaveInstallOptionsButtonControl.IsEnabled = false;

        AuthorLabelText.Text = CoreTools.Translate("Author");
        PublisherLabelText.Text = CoreTools.Translate("Publisher");
        LicenseLabelText.Text = CoreTools.Translate("License");
        UpdateDateLabelText.Text = CoreTools.Translate("Last updated:");
        InstallerTypeLabelText.Text = CoreTools.Translate("Installer Type");

        HomepageButtonControl.Content = CoreTools.Translate("Homepage");
        ReleaseNotesUrlButtonControl.Content = CoreTools.Translate("Release notes URL");
        ManifestUrlButtonControl.Content = CoreTools.Translate("Manifest");

        // Installer card
        InstallerTitleText.Text = CoreTools.Translate("Installer Type");
        InstallerHashLabelText.Text = CoreTools.Translate("Skip hash check");
        InstallerUrlLabelText.Text = CoreTools.Translate("Installer URL");
        InstallerSizeLabelText.Text = CoreTools.Translate("Size");
        DownloadInstallerButtonControl.Content = CoreTools.Translate("Download installer");
        if (_package.Manager.Capabilities.CanDownloadInstaller)
            DownloadInstallerButtonControl.IsVisible = true;

        // Dependencies card
        DependenciesTitleText.Text = CoreTools.Translate("Dependencies:");

        if (_installOptionsEditorView is not null)
        {
            InstallOptionsHostControl.Content = _installOptionsEditorView;
            InstallOptionsCardControl.IsVisible = true;
            InstallOptionsExpanderControl.IsExpanded = false;
            InstallOptionsExpanderControl.Expanded += InstallOptionsExpander_OnExpandedChanged;
            InstallOptionsExpanderControl.Collapsed += InstallOptionsExpander_OnExpandedChanged;
        }

        // Action dropdown flyout
        BuildActionFlyout();
    }

    private async Task LoadDetailsAsync()
    {
        try
        {
            var details = _package.Details;
            if (!details.IsPopulated)
            {
                await details.Load();
            }

            Dispatcher.UIThread.Post(() => PopulateDetails(details));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            Dispatcher.UIThread.Post(() =>
            {
                LoadingCardControl.IsVisible = true;
                LoadingStateText.Text = CoreTools.Translate("An interal error occurred. Please view the log for further details.");
            });
        }

        _ = LoadIconAsync();
        _ = LoadScreenshotsAsync();
    }

    private void PopulateDetails(IPackageDetails details)
    {
        LoadingCardControl.IsVisible = false;

        var visibleTags = details.Tags
            .Where(static tag => !string.IsNullOrWhiteSpace(tag))
            .Select(static tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (visibleTags.Length > 0)
        {
            TagsPanelControl.Children.Clear();
            foreach (var tag in visibleTags)
            {
                TagsPanelControl.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#1F6A92B8")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#4F6A92B8")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(999),
                    Margin = new Thickness(0, 0, 8, 8),
                    Padding = new Thickness(10, 4),
                    Child = new TextBlock
                    {
                        Text = tag,
                        FontSize = 12,
                        FontWeight = FontWeight.SemiBold,
                    },
                });
            }

            TagsCardControl.IsVisible = true;
        }

        if (!string.IsNullOrWhiteSpace(details.Description))
        {
            DescriptionText.Text = details.Description;
            DescriptionCardControl.IsVisible = true;
        }

        bool anyInfoRow = false;

        if (!string.IsNullOrWhiteSpace(details.Author))
        {
            AuthorValueText.Text = details.Author;
            AuthorRowControl.IsVisible = true;
            anyInfoRow = true;
        }

        if (!string.IsNullOrWhiteSpace(details.Publisher))
        {
            PublisherValueText.Text = details.Publisher;
            PublisherRowControl.IsVisible = true;
            anyInfoRow = true;
        }

        if (!string.IsNullOrWhiteSpace(details.License))
        {
            LicenseValueText.Text = details.LicenseUrl is not null
                ? $"{details.License} ({details.LicenseUrl})"
                : details.License;
            LicenseRowControl.IsVisible = true;
            anyInfoRow = true;
        }

        if (!string.IsNullOrWhiteSpace(details.UpdateDate))
        {
            UpdateDateValueText.Text = details.UpdateDate;
            UpdateDateRowControl.IsVisible = true;
            anyInfoRow = true;
        }

        if (!string.IsNullOrWhiteSpace(details.InstallerType))
        {
            InstallerTypeValueText.Text = details.InstallerType;
            InstallerTypeRowControl.IsVisible = true;
            anyInfoRow = true;
        }

        if (anyInfoRow)
        {
            InfoCardControl.IsVisible = true;
        }

        bool anyLink = false;

        if (details.HomepageUrl is not null)
        {
            _homepageUrl = details.HomepageUrl;
            HomepageButtonControl.IsVisible = true;
            anyLink = true;
        }

        if (details.ReleaseNotesUrl is not null)
        {
            _releaseNotesUrl = details.ReleaseNotesUrl;
            ReleaseNotesUrlButtonControl.IsVisible = true;
            anyLink = true;
        }

        if (details.ManifestUrl is not null)
        {
            _manifestUrl = details.ManifestUrl;
            ManifestUrlButtonControl.IsVisible = true;
            anyLink = true;
        }

        if (anyLink)
        {
            LinksCardControl.IsVisible = true;
        }

        if (!string.IsNullOrWhiteSpace(details.ReleaseNotes))
        {
            ReleaseNotesText.Text = details.ReleaseNotes;
            ReleaseNotesCardControl.IsVisible = true;
        }

        // Installer card
        bool anyInstallerRow = false;

        if (!string.IsNullOrWhiteSpace(details.InstallerHash))
        {
            InstallerHashValueText.Text = details.InstallerHash;
            InstallerHashRowControl.IsVisible = true;
            anyInstallerRow = true;
        }

        if (details.InstallerUrl is not null)
        {
            InstallerUrlValueText.Text = details.InstallerUrl.ToString();
            InstallerUrlRowControl.IsVisible = true;
            anyInstallerRow = true;
        }

        if (details.InstallerSize > 0)
        {
            InstallerSizeValueText.Text = CoreTools.FormatAsSize(details.InstallerSize);
            InstallerSizeRowControl.IsVisible = true;
            anyInstallerRow = true;
        }

        if (anyInstallerRow || _package.Manager.Capabilities.CanDownloadInstaller)
            InstallerCardControl.IsVisible = true;

        // Dependencies
        if (details.Dependencies.Count > 0)
        {
            foreach (var dep in details.Dependencies)
            {
                string label = dep.Name;
                if (!string.IsNullOrWhiteSpace(dep.Version))
                    label += " " + dep.Version;

                DependenciesPanelControl.Children.Add(new TextBlock
                {
                    Text = label,
                    Opacity = 0.82,
                    TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                });
            }
            DependenciesCardControl.IsVisible = true;
        }
    }

    private void HomepageButton_OnClick(object? sender, RoutedEventArgs e)
        => OpenUrl(_homepageUrl?.ToString());

    private void ReleaseNotesUrlButton_OnClick(object? sender, RoutedEventArgs e)
        => OpenUrl(_releaseNotesUrl?.ToString());

    private void ManifestUrlButton_OnClick(object? sender, RoutedEventArgs e)
        => OpenUrl(_manifestUrl?.ToString());

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e)
        => Close();

    private void ContributeScreenshotsButton_OnClick(object? sender, RoutedEventArgs e)
        => OpenUrl(ScreenshotsInfoUrl);

    private async void PreviousScreenshotButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_screenshotUris.Count <= 1)
        {
            return;
        }

        _selectedScreenshotIndex = (_selectedScreenshotIndex - 1 + _screenshotUris.Count) % _screenshotUris.Count;
        await ShowSelectedScreenshotAsync();
    }

    private async void NextScreenshotButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_screenshotUris.Count <= 1)
        {
            return;
        }

        _selectedScreenshotIndex = (_selectedScreenshotIndex + 1) % _screenshotUris.Count;
        await ShowSelectedScreenshotAsync();
    }

    private void InstallOptionsExpander_OnExpandedChanged(object? sender, RoutedEventArgs e)
    {
        SaveInstallOptionsButtonControl.IsEnabled = InstallOptionsExpanderControl.IsExpanded;
    }

    private async void SaveInstallOptionsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_installOptionsEditorView is null)
        {
            return;
        }

        try
        {
            SaveInstallOptionsButtonControl.IsEnabled = false;
            SaveInstallOptionsButtonControl.Content = CoreTools.Translate("Loading...");
            await _installOptionsEditorView.SaveAsync();
            SaveInstallOptionsButtonControl.Content = CoreTools.Translate("Save");
            global::UniGetUI.Avalonia.MainWindow.Instance?.ShowRuntimeNotification(
                CoreTools.Translate("Installation options"),
                CoreTools.Translate("Update succeeded"),
                global::UniGetUI.Avalonia.MainWindow.RuntimeNotificationLevel.Success);
            await Task.Delay(1200);
        }
        catch (Exception ex)
        {
            Logger.Error("An error occurred while saving install options");
            Logger.Error(ex);
            global::UniGetUI.Avalonia.MainWindow.Instance?.ShowRuntimeNotification(
                CoreTools.Translate("Installation options"),
                CoreTools.Translate("Update failed"),
                global::UniGetUI.Avalonia.MainWindow.RuntimeNotificationLevel.Error);
        }
        finally
        {
            SaveInstallOptionsButtonControl.Content = CoreTools.Translate("Save");
            SaveInstallOptionsButtonControl.IsEnabled = InstallOptionsExpanderControl.IsExpanded;
        }
    }

    private async void ShareButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_package.Source.IsVirtualManager || _package is InvalidImportedPackage)
        {
            global::UniGetUI.Avalonia.MainWindow.Instance?.ShowRuntimeNotification(
                CoreTools.Translate("Something went wrong"),
                CoreTools.Translate("Share this package"),
                global::UniGetUI.Avalonia.MainWindow.RuntimeNotificationLevel.Error);
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        await clipboard.SetTextAsync(BuildShareUrl(_package));
        global::UniGetUI.Avalonia.MainWindow.Instance?.ShowRuntimeNotification(
            CoreTools.Translate("Share this package"),
            CoreTools.Translate("Copy to clipboard"),
            global::UniGetUI.Avalonia.MainWindow.RuntimeNotificationLevel.Success);
    }

    private async void ActionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ActionButtonControl.IsEnabled = false;
        try
        {
            if (_pageMode == PackagePageMode.Installed
                && !await ConfirmUninstallAsync(_package))
            {
                ActionButtonControl.IsEnabled = true;
                return;
            }

            var options = await InstallOptionsFactory.LoadApplicableAsync(_package);
            AbstractOperation? operation = _pageMode switch
            {
                PackagePageMode.Discover => new InstallPackageOperation(_package, options),
                PackagePageMode.Updates => new UpdatePackageOperation(_package, options),
                PackagePageMode.Installed => new UninstallPackageOperation(_package, options),
                _ => null,
            };
            if (operation is null) return;
            AvaloniaOperationRegistry.Add(operation);
            _ = operation.MainThread();
            Close();
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            ActionButtonControl.IsEnabled = true;
        }
    }

    private async void DownloadInstallerButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!_package.Manager.Capabilities.CanDownloadInstaller) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = CoreTools.Translate("Download installer"),
            SuggestedFileName = _package.Id,
            DefaultExtension = "exe",
            FileTypeChoices =
            [
                new FilePickerFileType("Executable") { Patterns = ["*.exe"] },
                new FilePickerFileType("MSI") { Patterns = ["*.msi"] },
                new FilePickerFileType("Compressed file") { Patterns = ["*.zip"] },
                new FilePickerFileType("MSIX") { Patterns = ["*.msix"] },
                new FilePickerFileType("NuGet package") { Patterns = ["*.nupkg"] },
            ],
        });
        if (file is null) return;

        var op = new DownloadOperation(_package, file.Path.LocalPath);
        AvaloniaOperationRegistry.Add(op);
        _ = op.MainThread();
        Close();
    }

    private Task<bool> ConfirmUninstallAsync(IPackage package)
    {
        return UninstallConfirmationDialog.ConfirmAsync(this, package);
    }

    private async Task LoadIconAsync()
    {
        try
        {
            var iconUri = _package.GetIconUrlIfAny();
            if (iconUri is null || iconUri.Scheme == "ms-appx") return;

            using var client = new HttpClient(CoreTools.GenericHttpClientParameters);
            client.Timeout = TimeSpan.FromSeconds(10);
            var bytes = await client.GetByteArrayAsync(iconUri);
            using var ms = new MemoryStream(bytes);
            var bitmap = new Bitmap(ms);

            Dispatcher.UIThread.Post(() =>
            {
                PackageIconImageControl.Source = bitmap;
                PackageIconImageControl.IsVisible = true;
                PackageIconGlyphControl.IsVisible = false;
            });
        }
        catch
        {
            // ignore — glyph fallback stays visible
        }
    }

    private async Task LoadScreenshotsAsync()
    {
        try
        {
            var screenshots = await Task.Run(_package.GetScreenshots);
            Dispatcher.UIThread.Post(() =>
            {
                _screenshotUris = screenshots;
                _selectedScreenshotIndex = 0;
                ScreenshotsCardControl.IsVisible = true;
                ApplyScreenshotChrome();
            });

            if (screenshots.Count > 0)
            {
                await ShowSelectedScreenshotAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            Dispatcher.UIThread.Post(() =>
            {
                _screenshotUris = [];
                ScreenshotsCardControl.IsVisible = true;
                ApplyScreenshotChrome();
            });
        }
    }

    private async Task ShowSelectedScreenshotAsync()
    {
        if (_screenshotUris.Count == 0)
        {
            Dispatcher.UIThread.Post(ApplyScreenshotChrome);
            return;
        }

        Uri screenshotUri = _screenshotUris[_selectedScreenshotIndex];
        Dispatcher.UIThread.Post(() =>
        {
            ScreenshotsLoadingBarControl.IsVisible = true;
            PreviousScreenshotButtonControl.IsEnabled = false;
            NextScreenshotButtonControl.IsEnabled = false;
        });

        try
        {
            using var client = new HttpClient(CoreTools.GenericHttpClientParameters);
            client.Timeout = TimeSpan.FromSeconds(15);
            var bytes = await client.GetByteArrayAsync(screenshotUri);
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);

            Dispatcher.UIThread.Post(() =>
            {
                ScreenshotImageControl.Source = bitmap;
                ApplyScreenshotChrome();
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            Dispatcher.UIThread.Post(() =>
            {
                ScreenshotImageControl.Source = null;
                ApplyScreenshotChrome();
            });
        }
    }

    private void ApplyScreenshotChrome()
    {
        bool hasScreenshots = _screenshotUris.Count > 0;

        ScreenshotsCardControl.IsVisible = true;
        ScreenshotsImageCardControl.IsVisible = hasScreenshots;
        ScreenshotsBannerCardControl.IsVisible = true;
        ScreenshotsLoadingBarControl.IsVisible = false;

        if (!hasScreenshots)
        {
            ScreenshotsPipsText.Text = string.Empty;
            PreviousScreenshotButtonControl.IsVisible = false;
            NextScreenshotButtonControl.IsVisible = false;
            return;
        }

        PreviousScreenshotButtonControl.IsVisible = _screenshotUris.Count > 1;
        NextScreenshotButtonControl.IsVisible = _screenshotUris.Count > 1;
        PreviousScreenshotButtonControl.IsEnabled = _screenshotUris.Count > 1;
        NextScreenshotButtonControl.IsEnabled = _screenshotUris.Count > 1;
        ScreenshotsPipsText.Text = $"{_selectedScreenshotIndex + 1} / {_screenshotUris.Count}";
    }

    private void BuildActionFlyout()
    {
        if (_pageMode == PackagePageMode.None) return;

        var flyout = new MenuFlyout();
        var caps = _package.Manager.Capabilities;

        // Admin variant
        string adminLabel = _pageMode switch
        {
            PackagePageMode.Discover => CoreTools.Translate("Install as administrator"),
            PackagePageMode.Updates => CoreTools.Translate("Update as administrator"),
            PackagePageMode.Installed => CoreTools.Translate("Uninstall as administrator"),
            _ => CoreTools.Translate("Install as administrator"),
        };
        var adminItem = new MenuItem { Header = adminLabel, IsEnabled = caps.CanRunAsAdmin };
        adminItem.Click += async (_, _) =>
        {
            ActionButtonControl.IsEnabled = false;
            ActionDropdownButtonControl.IsEnabled = false;
            try
            {
                if (_pageMode == PackagePageMode.Installed
                    && !await ConfirmUninstallAsync(_package))
                {
                    ActionButtonControl.IsEnabled = true;
                    ActionDropdownButtonControl.IsEnabled = true;
                    return;
                }

                var options = await InstallOptionsFactory.LoadApplicableAsync(_package);
                options.RunAsAdministrator = true;
                AbstractOperation? op = _pageMode switch
                {
                    PackagePageMode.Discover => new InstallPackageOperation(_package, options),
                    PackagePageMode.Updates => new UpdatePackageOperation(_package, options),
                    PackagePageMode.Installed => new UninstallPackageOperation(_package, options),
                    _ => null,
                };
                if (op is null) return;
                AvaloniaOperationRegistry.Add(op);
                _ = op.MainThread();
                Close();
            }
            catch (Exception ex) { Logger.Error(ex); ActionButtonControl.IsEnabled = true; ActionDropdownButtonControl.IsEnabled = true; }
        };
        flyout.Items.Add(adminItem);

        // Interactive variant
        string interactiveLabel = _pageMode switch
        {
            PackagePageMode.Discover => CoreTools.Translate("Interactive installation"),
            PackagePageMode.Updates => CoreTools.Translate("Interactive update"),
            PackagePageMode.Installed => CoreTools.Translate("Interactive uninstall"),
            _ => CoreTools.Translate("Interactive installation"),
        };
        var interactiveItem = new MenuItem { Header = interactiveLabel, IsEnabled = caps.CanRunInteractively };
        interactiveItem.Click += async (_, _) =>
        {
            ActionButtonControl.IsEnabled = false;
            ActionDropdownButtonControl.IsEnabled = false;
            try
            {
                if (_pageMode == PackagePageMode.Installed
                    && !await ConfirmUninstallAsync(_package))
                {
                    ActionButtonControl.IsEnabled = true;
                    ActionDropdownButtonControl.IsEnabled = true;
                    return;
                }

                var options = await InstallOptionsFactory.LoadApplicableAsync(_package);
                options.InteractiveInstallation = true;
                AbstractOperation? op = _pageMode switch
                {
                    PackagePageMode.Discover => new InstallPackageOperation(_package, options),
                    PackagePageMode.Updates => new UpdatePackageOperation(_package, options),
                    PackagePageMode.Installed => new UninstallPackageOperation(_package, options),
                    _ => null,
                };
                if (op is null) return;
                AvaloniaOperationRegistry.Add(op);
                _ = op.MainThread();
                Close();
            }
            catch (Exception ex) { Logger.Error(ex); ActionButtonControl.IsEnabled = true; ActionDropdownButtonControl.IsEnabled = true; }
        };
        flyout.Items.Add(interactiveItem);

        // Skip hash check (non-Installed pages)
        if (_pageMode != PackagePageMode.Installed)
        {
            var item = new MenuItem
            {
                Header = CoreTools.Translate("Skip hash check"),
                IsEnabled = caps.CanSkipIntegrityChecks,
            };
            item.Click += async (_, _) =>
            {
                ActionButtonControl.IsEnabled = false;
                ActionDropdownButtonControl.IsEnabled = false;
                try
                {
                    var options = await InstallOptionsFactory.LoadApplicableAsync(_package);
                    options.SkipHashCheck = true;
                    AbstractOperation? op = _pageMode switch
                    {
                        PackagePageMode.Discover => new InstallPackageOperation(_package, options),
                        PackagePageMode.Updates => new UpdatePackageOperation(_package, options),
                        _ => null,
                    };
                    if (op is null) return;
                    AvaloniaOperationRegistry.Add(op);
                    _ = op.MainThread();
                    Close();
                }
                catch (Exception ex) { Logger.Error(ex); ActionButtonControl.IsEnabled = true; ActionDropdownButtonControl.IsEnabled = true; }
            };
            flyout.Items.Add(item);
        }

        // Cross-op actions
        if (_pageMode == PackagePageMode.Discover)
        {
            var upgradable = _package.GetUpgradablePackage();
            if (upgradable is not null)
            {
                if (flyout.Items.Count > 0) flyout.Items.Add(new Separator());
                var updateItem = new MenuItem
                {
                    Header = CoreTools.Translate("Update to version {0}", upgradable.NewVersionString)
                };
                updateItem.Click += async (_, _) =>
                {
                    ActionButtonControl.IsEnabled = false;
                    try
                    {
                        var options = await InstallOptionsFactory.LoadApplicableAsync(upgradable);
                        var op = new UpdatePackageOperation(upgradable, options);
                        AvaloniaOperationRegistry.Add(op);
                        _ = op.MainThread();
                        Close();
                    }
                    catch (Exception ex) { Logger.Error(ex); ActionButtonControl.IsEnabled = true; }
                };
                flyout.Items.Add(updateItem);
            }

            if (_package.GetInstalledPackages().Count > 0)
            {
                if (flyout.Items.Count > 0) flyout.Items.Add(new Separator());
                var item = new MenuItem { Header = CoreTools.Translate("Uninstall") };
                item.Click += async (_, _) =>
                {
                    ActionButtonControl.IsEnabled = false;
                    try
                    {
                        var installed = _package.GetInstalledPackages()[0];
                        if (!await ConfirmUninstallAsync(installed))
                        {
                            ActionButtonControl.IsEnabled = true;
                            return;
                        }

                        var options = await InstallOptionsFactory.LoadApplicableAsync(installed);
                        var op = new UninstallPackageOperation(installed, options);
                        AvaloniaOperationRegistry.Add(op);
                        _ = op.MainThread();
                        Close();
                    }
                    catch (Exception ex) { Logger.Error(ex); ActionButtonControl.IsEnabled = true; }
                };
                flyout.Items.Add(item);
            }
        }
        else if (_pageMode == PackagePageMode.Updates)
        {
            if (flyout.Items.Count > 0) flyout.Items.Add(new Separator());

            var uninstallItem = new MenuItem { Header = CoreTools.Translate("Uninstall package") };
            uninstallItem.Click += async (_, _) =>
            {
                ActionButtonControl.IsEnabled = false;
                try
                {
                    if (!await ConfirmUninstallAsync(_package))
                    {
                        ActionButtonControl.IsEnabled = true;
                        return;
                    }

                    var options = await InstallOptionsFactory.LoadApplicableAsync(_package);
                    var op = new UninstallPackageOperation(_package, options);
                    AvaloniaOperationRegistry.Add(op);
                    _ = op.MainThread();
                    Close();
                }
                catch (Exception ex) { Logger.Error(ex); ActionButtonControl.IsEnabled = true; }
            };
            flyout.Items.Add(uninstallItem);

            if (!_package.Source.IsVirtualManager)
            {
                var reinstallItem = new MenuItem { Header = CoreTools.Translate("Reinstall package") };
                reinstallItem.Click += async (_, _) =>
                {
                    ActionButtonControl.IsEnabled = false;
                    try
                    {
                        var options = await InstallOptionsFactory.LoadApplicableAsync(_package);
                        var op = new InstallPackageOperation(_package, options);
                        AvaloniaOperationRegistry.Add(op);
                        _ = op.MainThread();
                        Close();
                    }
                    catch (Exception ex) { Logger.Error(ex); ActionButtonControl.IsEnabled = true; }
                };
                flyout.Items.Add(reinstallItem);
            }
        }
        else if (_pageMode == PackagePageMode.Installed)
        {
            var removeDataItem = new MenuItem
            {
                Header = CoreTools.Translate("Uninstall and remove data"),
                IsEnabled = caps.CanRemoveDataOnUninstall,
            };
            removeDataItem.Click += async (_, _) =>
            {
                ActionButtonControl.IsEnabled = false;
                ActionDropdownButtonControl.IsEnabled = false;
                try
                {
                    if (!await ConfirmUninstallAsync(_package))
                    {
                        ActionButtonControl.IsEnabled = true;
                        ActionDropdownButtonControl.IsEnabled = true;
                        return;
                    }

                    var options = await InstallOptionsFactory.LoadApplicableAsync(_package);
                    options.RemoveDataOnUninstall = true;
                    var op = new UninstallPackageOperation(_package, options);
                    AvaloniaOperationRegistry.Add(op);
                    _ = op.MainThread();
                    Close();
                }
                catch (Exception ex) { Logger.Error(ex); ActionButtonControl.IsEnabled = true; ActionDropdownButtonControl.IsEnabled = true; }
            };
            flyout.Items.Add(removeDataItem);

            var upgradable = _package.GetUpgradablePackage();
            if (upgradable is not null)
            {
                if (flyout.Items.Count > 0) flyout.Items.Add(new Separator());
                var updateItem = new MenuItem
                {
                    Header = CoreTools.Translate("Update to version {0}", upgradable.NewVersionString)
                };
                updateItem.Click += async (_, _) =>
                {
                    ActionButtonControl.IsEnabled = false;
                    try
                    {
                        var options = await InstallOptionsFactory.LoadApplicableAsync(upgradable);
                        var op = new UpdatePackageOperation(upgradable, options);
                        AvaloniaOperationRegistry.Add(op);
                        _ = op.MainThread();
                        Close();
                    }
                    catch (Exception ex) { Logger.Error(ex); ActionButtonControl.IsEnabled = true; }
                };
                flyout.Items.Add(updateItem);
            }

            if (!_package.Source.IsVirtualManager)
            {
                if (flyout.Items.Count > 0 && upgradable is null) flyout.Items.Add(new Separator());
                var reinstallItem = new MenuItem { Header = CoreTools.Translate("Reinstall package") };
                reinstallItem.Click += async (_, _) =>
                {
                    ActionButtonControl.IsEnabled = false;
                    try
                    {
                        var options = await InstallOptionsFactory.LoadApplicableAsync(_package);
                        var op = new InstallPackageOperation(_package, options);
                        AvaloniaOperationRegistry.Add(op);
                        _ = op.MainThread();
                        Close();
                    }
                    catch (Exception ex) { Logger.Error(ex); ActionButtonControl.IsEnabled = true; }
                };
                flyout.Items.Add(reinstallItem);
            }
        }

        if (flyout.Items.Count > 0)
        {
            ActionDropdownButtonControl.Flyout = flyout;
            ActionDropdownButtonControl.IsVisible = true;
        }
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // ignore — best effort
        }
    }

    private static string BuildShareUrl(IPackage package)
    {
        return "https://marticliment.com/unigetui/share?"
            + "name=" + Uri.EscapeDataString(package.Name)
            + "&id=" + Uri.EscapeDataString(package.Id)
            + "&sourceName=" + Uri.EscapeDataString(package.Source.Name)
            + "&managerName=" + Uri.EscapeDataString(package.Manager.DisplayName);
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
