using CommunityToolkit.Mvvm.ComponentModel;
using UniGetUI.Core.Tools;
using UniGetUI.Extensions.Xcode;

namespace UniGetUI.Avalonia.ViewModels.Pages;

/// <summary>Turns what is known about an Xcode into the words the page shows.</summary>
internal static class XcodeText
{
    public static string Architecture(XcodeArchitecture architecture) => architecture switch
    {
        XcodeArchitecture.AppleSilicon => CoreTools.Translate("Apple Silicon"),
        XcodeArchitecture.Intel => CoreTools.Translate("Intel"),
        XcodeArchitecture.Universal => CoreTools.Translate("Universal"),
        _ => "",
    };

    /// <summary>"Build 17E192 · Apple Silicon"; whichever of them is known.</summary>
    public static string Details(string? build, XcodeArchitecture architecture)
    {
        List<string> parts = [];
        if (build is { Length: > 0 })
            parts.Add(CoreTools.Translate("Build {0}", build));
        if (Architecture(architecture) is { Length: > 0 } processors)
            parts.Add(processors);
        return string.Join(" · ", parts);
    }
}

/// <summary>An Xcode that is on this Mac.</summary>
public partial class InstalledXcodeItemViewModel(InstalledXcode xcode) : ViewModelBase
{
    public InstalledXcode Xcode { get; } = xcode;

    public string Version => Xcode.Version;
    public string Title => CoreTools.Translate("Xcode {0}", Xcode.Version);
    public string Location => Xcode.Path;
    public string DetailText => XcodeText.Details(Xcode.Build, Xcode.Architecture);

    /// <summary>The Xcode command line tools such as xcodebuild use.</summary>
    public bool IsSelected => Xcode.IsSelected;
    public bool CanSelect => !Xcode.IsSelected;
    public bool IsPrerelease => Xcode.IsPrerelease;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;
}

/// <summary>An Xcode that can be installed.</summary>
public sealed class AvailableXcodeItemViewModel(XcodeRelease release)
{
    public XcodeRelease Release { get; } = release;

    public string Version => Release.Version;
    public string Title => CoreTools.Translate("Xcode {0}", Release.Version);
    public string DetailText => XcodeText.Details(Release.Build, Release.Architecture);
    public bool IsPrerelease => Release.IsPrerelease;
    public bool CanInstall => !Release.IsIdentityUnverified;
}

/// <summary>A build in the browser, with each installed app represented by its own path.</summary>
public sealed class XcodeBrowserItemViewModel : ViewModelBase
{
    private bool _isOperationTarget;

    public XcodeBrowserItemViewModel(InstalledXcodeItemViewModel installed, XcodeCatalogDetails? details = null,
        string? stableCounterpart = null, XcodeRelease? downloadRelease = null)
    {
        InstalledItem = installed;
        Releases = [];
        Details = details;
        StableCounterpart = stableCounterpart;
        DownloadRelease = downloadRelease;
    }

    public XcodeBrowserItemViewModel(IReadOnlyList<XcodeRelease> releases, XcodeCatalogDetails? details = null)
    {
        ArgumentNullException.ThrowIfNull(releases);
        if (releases.Count == 0)
            throw new ArgumentException("At least one release is required.", nameof(releases));
        Releases = releases;
        AvailableItem = new AvailableXcodeItemViewModel(releases[0]);
        DownloadRelease = releases[0];
        Details = details;
    }

    public InstalledXcodeItemViewModel? InstalledItem { get; }
    public AvailableXcodeItemViewModel? AvailableItem { get; }
    public XcodeRelease? DownloadRelease { get; }
    public XcodeCatalogDetails? Details { get; }
    internal IReadOnlyList<XcodeRelease> Releases { get; }
    internal string? StableCounterpart { get; }

    internal string Key => InstalledItem is { } installed ? $"path:{installed.Location}" : AvailableKey(AvailableItem!.Release);
    internal static string AvailableKey(XcodeRelease release) => $"version:{release.Version}:build:{release.Build}";
    public string Version => InstalledItem?.Version ?? AvailableItem!.Version;
    public string Title => CoreTools.Translate("Xcode {0}", Version);
    public string DetailText => InstalledItem?.DetailText
        ?? (CanInstall ? AvailableItem!.DetailText is { Length: > 0 } details
                ? details : CoreTools.Translate("The build for this Mac is chosen when installing")
            : CoreTools.Translate("Exact build unknown"));
    public string Location => InstalledItem?.Location ?? "";
    public string ListedArchitecturesText
    {
        get
        {
            string architectures = string.Join(", ", Releases.Select(release => XcodeText.Architecture(release.Architecture))
                .Where(text => text.Length > 0).Distinct(StringComparer.Ordinal));
            return architectures.Length == 0
                ? CoreTools.Translate("The available architecture is unknown.")
                : CoreTools.Translate("Architectures available: {0}", architectures);
        }
    }

    public bool IsInstalled => InstalledItem is not null;
    public bool IsAvailable => AvailableItem is not null;
    public bool CanInstall => AvailableItem?.CanInstall == true;
    public bool CanDownload => DownloadRelease is { IsIdentityUnverified: false };
    public bool IsActive => InstalledItem?.IsSelected == true;
    public bool CanSelect => InstalledItem?.CanSelect == true;
    public bool IsPrerelease => InstalledItem?.IsPrerelease ?? AvailableItem!.IsPrerelease;
    public bool IsOperationTarget => _isOperationTarget;
    public string CancelOperationText => IsInstalled
        ? CoreTools.Translate("Cancel operation for Xcode {0} at {1}", Version, Location)
        : CoreTools.Translate("Cancel operation for Xcode {0}", Version);
    public bool HasStableCounterpart => StableCounterpart is not null;
    public string StableCounterpartText => StableCounterpart is { } version
        ? CoreTools.Translate(
            "A stable Xcode {0} for this architecture is installed or available. It is not selected automatically.", version)
        : "";
    public bool HasReleaseDetails => Details is not null;
    public bool HasReleaseNotes => Details?.ReleaseNotes is not null;
    public string ReleaseDateText => Details?.ReleaseDate is { } date
        ? CoreTools.Translate("Released {0}", date.ToString("D", System.Globalization.CultureInfo.CurrentCulture))
        : "";
    public string RequiredMacOSText => Details?.RequiredMacOS is { } required
        ? CoreTools.Translate("Requires macOS {0} or later", DisplayVersion(required))
        : CoreTools.Translate("Required macOS version is unknown.");
    public bool HasReleaseDate => Details?.ReleaseDate is not null;
    public bool HasSDKs => Details?.SDKs.Count > 0;
    public bool HasCompilers => Details?.Compilers.Count > 0;
    public bool HasSize => Details?.SizeMB is > 0;
    public bool HasSameBuildVersions => Details?.SameBuildVersions.Count > 0;
    public string SDKsText => CoreTools.Translate("SDKs: {0}", string.Join(", ", Details?.SDKs ?? []));
    public string CompilersText => CoreTools.Translate("Compilers: {0}", string.Join(", ", Details?.Compilers ?? []));
    public string SizeText => CoreTools.Translate("Download size: {0} MB", Details?.SizeMB ?? 0);
    public string SameBuildVersionsText => CoreTools.Translate(
        "The catalog reports the same build and architecture for: {0}", string.Join(", ", Details?.SameBuildVersions ?? []));
    public bool IsIncompatible => Details?.Compatibility.Status == XcodeCompatibility.Unsupported;
    public string CompatibilityText => Details?.Compatibility is { } compatibility ? compatibility.Status switch
    {
        XcodeCompatibility.Supported => CoreTools.Translate("The listed releases appear compatible with this macOS version."),
        XcodeCompatibility.Unsupported => CoreTools.Translate(
            "The listed releases require macOS {0} or later. This Mac is running macOS {1}.",
            DisplayVersion(compatibility.RequiredMacOS!), DisplayVersion(compatibility.CurrentMacOS)),
        _ => CoreTools.Translate("Compatibility with this macOS version is unknown."),
    } : CoreTools.Translate("Compatibility with this macOS version is unknown.");

    internal static string DisplayVersion(Version version)
        => version.Build > 0 ? version.ToString(3) : version.ToString(2);

    internal void SetOperationTarget(bool value) => SetProperty(ref _isOperationTarget, value, nameof(IsOperationTarget));

    internal bool Matches(string search)
        => search.Length == 0
           || Version.Contains(search, StringComparison.OrdinalIgnoreCase)
           || Location.Contains(search, StringComparison.OrdinalIgnoreCase)
           || InstalledItem?.Xcode.Build?.Contains(search, StringComparison.OrdinalIgnoreCase) == true
           || Releases.Any(release => release.Build?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);

    internal bool MatchesArchitecture(int filter)
    {
        XcodeArchitecture architecture = filter switch
        {
            1 => XcodeArchitecture.AppleSilicon,
            2 => XcodeArchitecture.Universal,
            3 => XcodeArchitecture.Intel,
            _ => XcodeArchitecture.Unspecified,
        };
        return filter == 0 || InstalledItem?.Xcode.Architecture == architecture
            || Releases.Any(release => release.Architecture == architecture);
    }
}

/// <summary>A group or an individual version in the optional hierarchical browser.</summary>
public partial class XcodeBrowserNodeViewModel : ViewModelBase
{
    private readonly Action<string, bool>? _expansionChanged;

    internal XcodeBrowserNodeViewModel(string key, string title, IReadOnlyList<XcodeBrowserNodeViewModel> children,
        bool isExpanded, Action<string, bool> expansionChanged)
    {
        Key = key;
        Title = title;
        Children = children;
        _isExpanded = isExpanded;
        _expansionChanged = expansionChanged;
    }

    internal XcodeBrowserNodeViewModel(XcodeBrowserItemViewModel item)
    {
        Key = item.Key;
        Title = item.Title;
        Item = item;
        Children = [];
    }

    internal string Key { get; }
    public string Title { get; }
    public XcodeBrowserItemViewModel? Item { get; }
    public IReadOnlyList<XcodeBrowserNodeViewModel> Children { get; }
    public bool IsGroup => Item is null;
    public bool HasInstalledChild => IsGroup && Children.Any(child => child.Item?.IsInstalled == true || child.HasInstalledChild);
    public bool HasActiveChild => IsGroup && Children.Any(child => child.Item?.IsActive == true || child.HasActiveChild);
    public bool HasInactiveInstalledChild => HasInstalledChild && !HasActiveChild;

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value) => _expansionChanged?.Invoke(Key, value);
}
