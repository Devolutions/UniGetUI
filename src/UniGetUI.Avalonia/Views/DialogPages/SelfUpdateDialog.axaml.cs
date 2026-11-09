using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.DialogPages;

public partial class SelfUpdateDialog : ImmersiveDialog
{
    private static readonly Lazy<Bitmap> AppIcon =
        new(() => new Bitmap(AssetLoader.Open(new Uri("avares://UniGetUI/Assets/icon.png"))));

    private readonly AvaloniaAutoUpdater.PendingSelfUpdate _update;
    private bool _updateChosen;
    private bool _updateWithdrawn;
    private bool _closing;

    internal SelfUpdateDialog(AvaloniaAutoUpdater.PendingSelfUpdate update)
    {
        _update = update;

        InitializeComponent();

        string newVersion = ReleaseNotesLinks.ShortVersion(update.VersionName);
        Title = CoreTools.Translate("UniGetUI {0} is available", newVersion);
        HeaderContent = BuildHeader(Title, update, newVersion);
        SkipVersionCheck.Content = CoreTools.Translate("Skip this version");

        SetButtonLabel(UpdateNowButton, CoreTools.Translate("Update now"));
        UpdateNowMenuItem.Header = CoreTools.Translate("Update now");
        DownloadInBrowserMenuItem.Header = CoreTools.Translate("Download the installer in my browser");
        if (AvaloniaAutoUpdater.IsInstallOnQuitRequested)
        {
            UpdateOnQuitMenuItem.Header = CoreTools.Translate("UniGetUI will update when you quit it");
            UpdateOnQuitMenuItem.IsEnabled = false;
        }
        else
        {
            UpdateOnQuitMenuItem.Header = CoreTools.Translate("Update when I quit UniGetUI");
        }

        UpdateNowButton.Click += (_, _) => UpdateNow();
        UpdateNowMenuItem.Click += (_, _) => UpdateNow();
        UpdateOnQuitMenuItem.Click += (_, _) => UpdateOnQuit();
        DownloadInBrowserMenuItem.Click += (_, _) => DownloadInBrowser();

        Browser.OpenNewWindowsInBrowser = true;
        Browser.SetBrowserVisible(false);
        if (OperatingSystem.IsWindows())
            Browser.KeepNavigationInside = ReleaseNotesLinks.IsReleaseNotesPage;
        Browser.Navigate(ReleaseNotesLinks.UpdateDialogUri(ThemeHelper.IsDark));

        Closing += (_, e) =>
        {
            if (e.Cancel || _closing)
                return;
            _closing = true;
            Browser.SetBrowserVisible(false);
            if (!_updateChosen && !_updateWithdrawn && SkipVersionCheck.IsChecked == true)
                AvaloniaAutoUpdater.SkipVersion(_update.VersionName);
        };
    }

    private static Control BuildHeader(string title, AvaloniaAutoUpdater.PendingSelfUpdate update, string newVersion)
    {
        string versions = CoreTools.Translate(
            "You have {0} → {1}",
            ReleaseNotesLinks.ShortVersion(CoreData.VersionName),
            newVersion);
        if (update.InstallerSizeBytes > 0)
            versions += " · " + CoreTools.FormatAsSize(update.InstallerSizeBytes);

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var versionsBlock = new TextBlock
        {
            Text = versions,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        versionsBlock.Bind(
            TextBlock.ForegroundProperty,
            versionsBlock.GetResourceObservable("TextFillColorSecondaryBrush"));

        var icon = new Image
        {
            Source = AppIcon.Value,
            Width = 40,
            Height = 40,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapInterpolationMode(icon, BitmapInterpolationMode.HighQuality);
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);

        var texts = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { titleBlock, versionsBlock },
        };
        Grid.SetColumn(texts, 1);

        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 14,
            Margin = new Thickness(0, 16, 0, 10),
            Children = { icon, texts },
        };
    }

    private static void SetButtonLabel(Button button, string label)
    {
        button.Content = label;
        AutomationProperties.SetName(button, label);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        AvaloniaAutoUpdater.PendingUpdateChanged += OnPendingUpdateChanged;
        Dispatcher.UIThread.Post(() => UpdateNowButton.Focus(), DispatcherPriority.Background);
        OnPendingUpdateChanged();
    }

    protected override void OnPresented(EventArgs e)
    {
        base.OnPresented(e);
        if (!_closing)
            Browser.SetBrowserVisible(true);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        AvaloniaAutoUpdater.PendingUpdateChanged -= OnPendingUpdateChanged;
        Browser.Shutdown();
    }

    private void OnPendingUpdateChanged()
    {
        if (_closing || AvaloniaAutoUpdater.PendingUpdate?.VersionName == _update.VersionName)
            return;
        _updateWithdrawn = true;
        Close();
    }

    private void UpdateNow()
    {
        _updateChosen = true;
        Close();
        AvaloniaAutoUpdater.TriggerInstall();
    }

    private void UpdateOnQuit()
    {
        _updateChosen = true;
        AvaloniaAutoUpdater.RequestInstallOnQuit();
        Close();
    }

    private void DownloadInBrowser()
    {
        CoreTools.Launch(ReleaseNotesLinks.BrowserDownloadUrl(
            _update.VersionName,
            _update.InstallerDownloadUrl,
            OperatingSystem.IsWindows()));
    }
}
