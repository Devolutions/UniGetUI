using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Managers.InstallerFeeds;
#if WINDOWS
using UniGetUI.PackageEngine.Managers.WinInstallerManager;
#else
using UniGetUI.PackageEngine.Managers.MacPkgManager;
#endif

namespace UniGetUI.Avalonia.ViewModels;

/// <summary>One line of the feed preview: what it is, then its details.</summary>
public sealed record FeedPreviewRow(string Title, string Detail);

/// <summary>
/// Edits a catalog's package feed (where IT drops .pkg, .dmg and .zip files for macOS, .exe and .msi for Windows) and previews what
/// it publishes, so a typo or an unreadable file shows up before the catalog is deployed.
/// </summary>
public partial class PkgFeedEditorViewModel : ViewModelBase
{
    private readonly Func<Task<string?>> _pickFolder;

    public PkgFeedEditorViewModel(string? location, bool allowUnsignedPackages, Func<Task<string?>> pickFolder)
    {
        _location = location ?? "";
        _allowUnsignedPackages = allowUnsignedPackages;
        _pickFolder = pickFolder;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    private string _location;

    [ObservableProperty]
    private bool _allowUnsignedPackages;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    private bool _isChecking;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string _warning = "";

    public ObservableCollection<FeedPreviewRow> Packages { get; } = [];
    public ObservableCollection<FeedPreviewRow> Problems { get; } = [];

    public bool HasWarning => Warning.Length > 0;

    /// <summary>An empty location is valid: it removes the catalog's feed.</summary>
    public bool IsValid => Location.Trim().Length == 0 || IsValidLocation(Location.Trim());

    public string ValidationMessage => IsValid
        ? ""
        : CoreTools.Translate("Enter an https address or the full path of a folder.");

    public static bool CanPreview => true;

    public static string FeedDescription => OperatingSystem.IsWindows()
        ? CoreTools.Translate("Every .exe and .msi file in this folder or web address appears in the catalog. Leave it empty for a catalog without a feed.")
        : CoreTools.Translate("Every .pkg, .dmg and .zip file in this folder or web address appears in the catalog. Leave it empty for a catalog without a feed.");

    public static string UnsignedDescription => OperatingSystem.IsWindows()
        ? CoreTools.Translate("Allow installers without a valid Authenticode signature (for your own packages; keep the feed where only administrators can change it)")
        : CoreTools.Translate("Allow packages that are not signed and notarized (for your own packages; keep the feed where only administrators can change it)");

    public static string LocationPlaceholder => OperatingSystem.IsWindows()
        ? @"\\server\software\windows"
        : "https://software.example.com/mac/";

    private static bool IsValidLocation(string text) => FeedLocation.TryParse(text, out _);

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _pickFolder() is { Length: > 0 } folder)
            Location = folder;
    }

    private bool CanCheck() => CanPreview && !IsChecking && Location.Trim().Length > 0 && IsValid;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        IsChecking = true;
        Packages.Clear();
        Problems.Clear();
        Warning = "";
        Summary = CoreTools.Translate("Reading the feed...");
        try
        {
            string location = Location.Trim();
#if WINDOWS
            var inspection = await Task.Run(() => WinInstallerFeedInspector.Inspect(location));
#else
            var inspection = await Task.Run(() => PkgFeedInspector.Inspect(location));
#endif
            foreach (var package in inspection.Packages.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
                Packages.Add(new FeedPreviewRow($"{package.Name} {package.Version}",
                    $"{package.Id} · {package.Kind} · {package.File}"
                    + (package.Architectures.Count > 0 ? " · " + string.Join(", ", package.Architectures) : "")));
            foreach (var problem in inspection.Problems)
                Problems.Add(new FeedPreviewRow(problem.File, problem.Reason));
            Warning = inspection.Warning ?? "";
            Summary = CoreTools.Translate("{0} packages found, {1} files skipped.", Packages.Count, Problems.Count);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not read the package feed {Location}: {ex.Message}");
            Summary = CoreTools.Translate("The feed could not be read: {0}", ex.Message);
        }
        finally
        {
            IsChecking = false;
        }
    }
}
