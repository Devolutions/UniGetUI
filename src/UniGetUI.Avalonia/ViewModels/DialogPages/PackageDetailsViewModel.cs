using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Avalonia.ViewModels;

public partial class PackageDetailsViewModel : ObservableObject
{
    public event EventHandler? CloseRequested;
    /// <summary>Raised on the UI thread after details have been loaded so the view
    /// can (re)populate the inline rich-text blocks.</summary>
    public event EventHandler? DetailsLoaded;

    public readonly IPackage Package;
    public readonly OperationType OperationRole;

    // ── Header ─────────────────────────────────────────────────────────────────
    public string PackageName { get; }
    public string SourceDisplay { get; }

    [ObservableProperty]
    private Bitmap? _packageIcon;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoaded))]
    private bool _isLoading = true;

    public bool IsLoaded => !IsLoading;

    // ── Tags ───────────────────────────────────────────────────────────────────
    public ObservableCollection<string> Tags { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTags))]
    private int _tagCount;

    public bool HasTags => TagCount > 0;

    // ── Description ────────────────────────────────────────────────────────────
    [ObservableProperty]
    private string _description = CoreTools.Translate("Loading...");

    // ── Basic info (raw values exposed; the view builds the inline rich text) ──
    [ObservableProperty]
    private string _versionDisplay = "";

    [ObservableProperty]
    private Uri? _homepageUrl;

    [ObservableProperty]
    private string _author = CoreTools.Translate("Loading...");
    [ObservableProperty]
    private string _publisher = CoreTools.Translate("Loading...");

    [ObservableProperty]
    private string? _licenseName;
    [ObservableProperty]
    private Uri? _licenseUrl;

    // ── Actions ────────────────────────────────────────────────────────────────
    public string MainActionLabel { get; }
    public string AsAdminLabel { get; }
    public string InteractiveLabel { get; }
    public string SkipHashOrRemoveDataLabel { get; }
    public bool CanRunAsAdmin { get; }
    public bool CanRunInteractively { get; }
    public bool CanSkipHashOrRemoveData { get; }

    // ── Extended details ───────────────────────────────────────────────────────
    public string PackageId { get; }

    [ObservableProperty]
    private Uri? _manifestUrl;

    [ObservableProperty]
    private string _installerHashLabel = CoreTools.Translate("Installer SHA256") + ":";
    [ObservableProperty]
    private string _installerHash = CoreTools.Translate("Loading...");
    [ObservableProperty]
    private string _installerType = CoreTools.Translate("Loading...");
    [ObservableProperty]
    private Uri? _installerUrl;
    [ObservableProperty]
    private string _installerSize = "";

    public bool CanDownloadInstaller { get; }

    [ObservableProperty]
    private string _updateDate = CoreTools.Translate("Loading...");

    // ── Dependencies ───────────────────────────────────────────────────────────
    public bool CanListDependencies { get; }
    public ObservableCollection<DependencyViewModel> Dependencies { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDependenciesList))]
    private bool _hasDependencyNote = true;

    public bool HasDependenciesList => !HasDependencyNote;

    [ObservableProperty]
    private string _dependencyNote = "";

    // ── Screenshots ────────────────────────────────────────────────────────────
    public ObservableCollection<Bitmap> Screenshots { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScreenshots))]
    private int _screenshotCount;

    public bool HasScreenshots => ScreenshotCount > 0;

    [ObservableProperty]
    private int _selectedScreenshotIndex;

    // ── Release notes ──────────────────────────────────────────────────────────
    [ObservableProperty]
    private string _releaseNotes = CoreTools.Translate("Loading...");
    [ObservableProperty]
    private Uri? _releaseNotesUrl;

    // ── Translated labels ──────────────────────────────────────────────────────
    public string LabelVersion { get; }
    public string LabelHomepage { get; } = CoreTools.Translate("Homepage");
    public string LabelAuthor { get; } = CoreTools.Translate("Author");
    public string LabelPublisher { get; } = CoreTools.Translate("Publisher");
    public string LabelLicense { get; } = CoreTools.Translate("License");
    public string LabelSource { get; } = CoreTools.Translate("Package Manager");
    public string LabelPackageId { get; } = CoreTools.Translate("Package ID");
    public string LabelManifest { get; } = CoreTools.Translate("Manifest");
    public string LabelInstallerType { get; } = CoreTools.Translate("Installer Type");
    public string LabelInstallerUrl { get; } = CoreTools.Translate("Installer URL");
    public string LabelUpdateDate { get; } = CoreTools.Translate("Last updated");
    public string LabelDependencies { get; } = CoreTools.Translate("Dependencies");
    public string LabelReleaseNotes { get; } = CoreTools.Translate("Release notes");
    public string LabelReleaseNotesUrl { get; } = CoreTools.Translate("Release notes URL");
    public string LabelDownloadInstaller { get; } = CoreTools.Translate("Download installer");
    public string LabelInstallerNotAvailable { get; } = CoreTools.Translate("Installer not available");
    public string LabelNotAvailable { get; } = CoreTools.Translate("Not available");
    public string LabelNoDependencies { get; } = CoreTools.Translate("No dependencies specified");
    public string LabelInstallationOptions { get; } = CoreTools.Translate("Installation options");
    public string LabelSave { get; } = CoreTools.Translate("Save");
    public string LabelContributorBanner { get; } = CoreTools.Translate(
        "This package has no screenshots or is missing the icon? Contribute to UniGetUI by adding the missing icons and screenshots to our open, public database.");
    public string LabelContribute { get; } = CoreTools.Translate("Become a contributor");

    public PackageDetailsViewModel(IPackage package, OperationType role)
    {
        if (role == OperationType.None) role = OperationType.Install;

        Package = package;
        OperationRole = role;
        PackageName = package.Name;
        PackageId = package.Id;
        SourceDisplay = package.Source.AsString_DisplayName;

        CanDownloadInstaller = package.Manager.Capabilities.CanDownloadInstaller;
        CanListDependencies = package.Manager.Capabilities.CanListDependencies;

        var caps = package.Manager.Capabilities;
        CanRunAsAdmin = caps.CanRunAsAdmin;
        CanRunInteractively = caps.CanRunInteractively;

        var available = package.GetAvailablePackage();
        var upgradable = package.GetUpgradablePackage();
        var installed = upgradable?.GetInstalledPackages().FirstOrDefault();

        if (role == OperationType.Install)
        {
            MainActionLabel = CoreTools.Translate("Install");
            LabelVersion = CoreTools.Translate("Version");
            VersionDisplay = available?.VersionString ?? package.VersionString;
            AsAdminLabel = CoreTools.Translate("Install as administrator");
            InteractiveLabel = CoreTools.Translate("Interactive installation");
            SkipHashOrRemoveDataLabel = CoreTools.Translate("Skip hash check");
            CanSkipHashOrRemoveData = caps.CanSkipIntegrityChecks;
        }
        else if (role == OperationType.Update)
        {
            MainActionLabel = CoreTools.Translate(
                "Update to version {0}", upgradable?.NewVersionString ?? package.NewVersionString);
            LabelVersion = CoreTools.Translate("Installed Version");
            VersionDisplay = (upgradable?.VersionString ?? package.VersionString)
                             + " ➤ "
                             + (upgradable?.NewVersionString ?? package.NewVersionString);
            AsAdminLabel = CoreTools.Translate("Update as administrator");
            InteractiveLabel = CoreTools.Translate("Interactive update");
            SkipHashOrRemoveDataLabel = CoreTools.Translate("Skip hash check");
            CanSkipHashOrRemoveData = caps.CanSkipIntegrityChecks;
        }
        else
        {
            MainActionLabel = CoreTools.Translate("Uninstall");
            LabelVersion = CoreTools.Translate("Installed Version");
            VersionDisplay = installed?.VersionString ?? package.VersionString;
            AsAdminLabel = CoreTools.Translate("Uninstall as administrator");
            InteractiveLabel = CoreTools.Translate("Interactive uninstall");
            SkipHashOrRemoveDataLabel = CoreTools.Translate("Uninstall and remove data");
            CanSkipHashOrRemoveData = caps.CanRemoveDataOnUninstall;
        }
    }

    [RelayCommand]
    private void PreviousScreenshot()
    {
        if (SelectedScreenshotIndex > 0)
            SelectedScreenshotIndex--;
    }

    [RelayCommand]
    private void NextScreenshot()
    {
        if (SelectedScreenshotIndex < ScreenshotCount - 1)
            SelectedScreenshotIndex++;
    }

    public async Task LoadDetailsAsync()
    {
        _ = LoadIconAsync();
        _ = LoadScreenshotsAsync();

        var details = Package.Details;
        if (!details.IsPopulated)
            await details.Load();

        IsLoading = false;

        Description = details.Description ?? CoreTools.Translate("Not available");
        HomepageUrl = details.HomepageUrl;
        Author = details.Author ?? CoreTools.Translate("Not available");
        Publisher = details.Publisher ?? CoreTools.Translate("Not available");

        LicenseName = details.License;
        LicenseUrl = details.LicenseUrl;

        ManifestUrl = details.ManifestUrl;

        if (Package.Manager.Properties.Name.Equals("chocolatey", StringComparison.OrdinalIgnoreCase))
            InstallerHashLabel = CoreTools.Translate("Installer SHA512") + ":";

        InstallerHash = details.InstallerHash ?? CoreTools.Translate("Not available");
        InstallerType = details.InstallerType ?? CoreTools.Translate("Not available");
        InstallerUrl = details.InstallerUrl;
        InstallerSize = details.InstallerSize > 0
            ? CoreTools.FormatAsSize(details.InstallerSize, 2)
            : CoreTools.Translate("Unknown size");
        UpdateDate = details.UpdateDate ?? CoreTools.Translate("Not available");

        ReleaseNotes = details.ReleaseNotes ?? CoreTools.Translate("Not available");
        ReleaseNotesUrl = details.ReleaseNotesUrl;

        if (!CanListDependencies)
        {
            DependencyNote = CoreTools.Translate("Not available");
            HasDependencyNote = true;
        }
        else if (details.Dependencies.Any())
        {
            HasDependencyNote = false;
            Dependencies.Clear();
            foreach (var dep in details.Dependencies)
                Dependencies.Add(new DependencyViewModel(dep));
        }
        else
        {
            DependencyNote = CoreTools.Translate("No dependencies specified");
            HasDependencyNote = true;
        }

        Tags.Clear();
        foreach (var tag in details.Tags)
            Tags.Add(tag);
        TagCount = Tags.Count;

        DetailsLoaded?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadIconAsync()
    {
        try
        {
            var uri = await Task.Run(Package.GetIconUrlIfAny);
            if (uri is not null)
            {
                Bitmap? bitmap = null;
                if (uri.IsFile)
                {
                    bitmap = await Task.Run(() => new Bitmap(uri.LocalPath));
                }
                else if (uri.Scheme is "http" or "https")
                {
                    using var http = new HttpClient(CoreTools.GenericHttpClientParameters);
                    var bytes = await http.GetByteArrayAsync(uri);
                    using var ms = new MemoryStream(bytes);
                    bitmap = new Bitmap(ms);
                }

                if (bitmap is not null)
                {
                    PackageIcon = bitmap;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[PackageDetailsViewModel] Failed to load icon: {ex.Message}");
        }

        try
        {
            using var stream = AssetLoader.Open(
                new Uri("avares://UniGetUI/Assets/package_color.png"));
            PackageIcon = new Bitmap(stream);
        }
        catch { }
    }

    private async Task LoadScreenshotsAsync()
    {
        try
        {
            var uris = await Task.Run(Package.GetScreenshots);
            if (!uris.Any()) return;

            using var http = new HttpClient(CoreTools.GenericHttpClientParameters);
            foreach (var uri in uris)
            {
                try
                {
                    var bytes = await http.GetByteArrayAsync(uri);
                    using var ms = new MemoryStream(bytes);
                    var bmp = new Bitmap(ms);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        Screenshots.Add(bmp);
                        ScreenshotCount = Screenshots.Count;
                    });
                }
                catch { /* skip failed screenshots */ }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[PackageDetailsViewModel] Failed to load screenshots: {ex.Message}");
        }
    }

    [RelayCommand]
    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    [RelayCommand]
    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);
}

public class DependencyViewModel
{
    public string DisplayText { get; }

    public DependencyViewModel(IPackageDetails.Dependency dep)
    {
        var text = $"  • {dep.Name}";
        if (!string.IsNullOrEmpty(dep.Version))
            text += $" v{dep.Version}";
        text += dep.Mandatory
            ? $" ({CoreTools.Translate("mandatory")})"
            : $" ({CoreTools.Translate("optional")})";
        DisplayText = text;
    }
}
