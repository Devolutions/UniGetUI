using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Avalonia.ViewModels;

public partial class PackageDetailsViewModel : ObservableObject
{
    public event EventHandler? CloseRequested;

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

    // ── Basic info ─────────────────────────────────────────────────────────────
    [ObservableProperty]
    private string _versionDisplay = "";

    [ObservableProperty]
    private string _homepageText = CoreTools.Translate("Loading...");
    [ObservableProperty]
    private bool _hasHomepageUrl;

    [ObservableProperty]
    private string _author = CoreTools.Translate("Loading...");
    [ObservableProperty]
    private string _publisher = CoreTools.Translate("Loading...");

    [ObservableProperty]
    private string _licenseText = CoreTools.Translate("Loading...");
    [ObservableProperty]
    private bool _hasLicenseUrl;

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
    private string _manifestText = CoreTools.Translate("Loading...");
    [ObservableProperty]
    private bool _hasManifestUrl;

    [ObservableProperty]
    private string _installerHashLabel = CoreTools.Translate("Installer SHA256") + ":";
    [ObservableProperty]
    private string _installerHash = CoreTools.Translate("Loading...");
    [ObservableProperty]
    private string _installerType = CoreTools.Translate("Loading...");
    [ObservableProperty]
    private string _installerUrlText = CoreTools.Translate("Loading...");
    [ObservableProperty]
    private bool _hasInstallerUrl;
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

    // ── Release notes ──────────────────────────────────────────────────────────
    [ObservableProperty]
    private string _releaseNotes = CoreTools.Translate("Loading...");
    [ObservableProperty]
    private string _releaseNotesUrlText = CoreTools.Translate("Loading...");
    [ObservableProperty]
    private bool _hasReleaseNotesUrl;

    // ── Translated labels ──────────────────────────────────────────────────────
    public string LabelVersion { get; }
    public string LabelHomepage { get; } = CoreTools.Translate("Homepage") + ":";
    public string LabelAuthor { get; } = CoreTools.Translate("Author") + ":";
    public string LabelPublisher { get; } = CoreTools.Translate("Publisher") + ":";
    public string LabelLicense { get; } = CoreTools.Translate("License") + ":";
    public string LabelPackageId { get; } = CoreTools.Translate("Package ID") + ":";
    public string LabelManifest { get; } = CoreTools.Translate("Manifest") + ":";
    public string LabelInstallerType { get; } = CoreTools.Translate("Installer Type") + ":";
    public string LabelInstallerSize { get; } = CoreTools.Translate("Size") + ":";
    public string LabelInstallerUrl { get; } = CoreTools.Translate("Installer URL") + ":";
    public string LabelUpdateDate { get; } = CoreTools.Translate("Last updated:");
    public string LabelReleaseNotesUrl { get; } = CoreTools.Translate("Release notes URL") + ":";
    public string LabelOpen { get; } = CoreTools.Translate("Open");
    public string LabelClose { get; } = CoreTools.Translate("Close");
    public string HeaderDetails { get; } = CoreTools.Translate("Package details");
    public string HeaderDeps { get; } = CoreTools.Translate("Dependencies:");
    public string HeaderReleaseNotes { get; } = CoreTools.Translate("Release notes");

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
            LabelVersion = CoreTools.Translate("Version") + ":";
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
            LabelVersion = CoreTools.Translate("Installed Version") + ":";
            VersionDisplay = (upgradable?.VersionString ?? package.VersionString)
                                         + " \u27a4 "
                                         + (upgradable?.NewVersionString ?? package.NewVersionString);
            AsAdminLabel = CoreTools.Translate("Update as administrator");
            InteractiveLabel = CoreTools.Translate("Interactive update");
            SkipHashOrRemoveDataLabel = CoreTools.Translate("Skip hash check");
            CanSkipHashOrRemoveData = caps.CanSkipIntegrityChecks;
        }
        else
        {
            MainActionLabel = CoreTools.Translate("Uninstall");
            LabelVersion = CoreTools.Translate("Installed Version") + ":";
            VersionDisplay = installed?.VersionString ?? package.VersionString;
            AsAdminLabel = CoreTools.Translate("Uninstall as administrator");
            InteractiveLabel = CoreTools.Translate("Interactive uninstall");
            SkipHashOrRemoveDataLabel = CoreTools.Translate("Uninstall and remove data");
            CanSkipHashOrRemoveData = caps.CanRemoveDataOnUninstall;
        }
    }

    public async Task LoadDetailsAsync()
    {
        _ = LoadIconAsync();

        var details = Package.Details;
        if (!details.IsPopulated)
            await details.Load();

        IsLoading = false;

        Description = details.Description ?? CoreTools.Translate("Not available");
        HomepageText = details.HomepageUrl?.ToString() ?? CoreTools.Translate("Not available");
        HasHomepageUrl = details.HomepageUrl is not null;
        Author = details.Author ?? CoreTools.Translate("Not available");
        Publisher = details.Publisher ?? CoreTools.Translate("Not available");

        if (details.License is not null && details.LicenseUrl is not null)
            LicenseText = $"{details.License} ({details.LicenseUrl})";
        else if (details.License is not null)
            LicenseText = details.License;
        else if (details.LicenseUrl is not null)
            LicenseText = details.LicenseUrl.ToString();
        else
            LicenseText = CoreTools.Translate("Not available");
        HasLicenseUrl = details.LicenseUrl is not null;

        ManifestText = details.ManifestUrl?.ToString() ?? CoreTools.Translate("Not available");
        HasManifestUrl = details.ManifestUrl is not null;

        if (Package.Manager.Properties.Name.Equals("chocolatey", StringComparison.OrdinalIgnoreCase))
            InstallerHashLabel = CoreTools.Translate("Installer SHA512") + ":";

        InstallerHash = details.InstallerHash ?? CoreTools.Translate("Not available");
        InstallerType = details.InstallerType ?? CoreTools.Translate("Not available");
        InstallerUrlText = details.InstallerUrl?.ToString() ?? CoreTools.Translate("Not available");
        HasInstallerUrl = details.InstallerUrl is not null;
        InstallerSize = details.InstallerSize > 0
            ? CoreTools.FormatAsSize(details.InstallerSize, 2)
            : CoreTools.Translate("Unknown size");
        UpdateDate = details.UpdateDate ?? CoreTools.Translate("Not available");

        ReleaseNotes = details.ReleaseNotes ?? CoreTools.Translate("Not available");
        ReleaseNotesUrlText = details.ReleaseNotesUrl?.ToString() ?? CoreTools.Translate("Not available");
        HasReleaseNotesUrl = details.ReleaseNotesUrl is not null;

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
    }

    private async Task LoadIconAsync()
    {
        try
        {
            var iconUrl = await Task.Run(Package.GetIconUrl);
            if (iconUrl is not null)
            {
                using var http = new HttpClient(CoreTools.GenericHttpClientParameters);
                var bytes = await http.GetByteArrayAsync(iconUrl);
                using var ms = new MemoryStream(bytes);
                PackageIcon = new Bitmap(ms);
                return;
            }
        }
        catch { /* icon is optional */ }

        try
        {
            using var stream = AssetLoader.Open(
                new Uri("avares://UniGetUI.Avalonia/Assets/package_color.png"));
            PackageIcon = new Bitmap(stream);
        }
        catch { }
    }

    [RelayCommand]
    private static void OpenUrl(string? url)
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
        var text = $"  \u2022 {dep.Name}";
        if (!string.IsNullOrEmpty(dep.Version))
            text += $" v{dep.Version}";
        text += dep.Mandatory
            ? $" ({CoreTools.Translate("mandatory")})"
            : $" ({CoreTools.Translate("optional")})";
        DisplayText = text;
    }
}
