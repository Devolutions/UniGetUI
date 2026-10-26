using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
#if !WINDOWS
using UniGetUI.PackageEngine.Managers.MacPkgManager;
#endif

namespace UniGetUI.Avalonia.ViewModels;

/// <summary>One line of the feed preview: what it is, then its details.</summary>
public sealed record FeedPreviewRow(string Title, string Detail);

/// <summary>
/// Edits a catalog's package feed (where IT drops macOS .pkg, .dmg and .zip files) and previews what
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

    /// <summary>Previews read feeds with the macOS reader, which only macOS and Linux builds include.</summary>
    public static bool CanPreview => !OperatingSystem.IsWindows();

    private static bool IsValidLocation(string text)
    {
#if WINDOWS
        return Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && (uri.IsFile || uri.Scheme == Uri.UriSchemeHttps);
#else
        return PkgFeedInspector.IsValidLocation(text);
#endif
    }

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
#if !WINDOWS
            string location = Location.Trim();
            var inspection = await Task.Run(() => PkgFeedInspector.Inspect(location));
            foreach (var package in inspection.Packages.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
                Packages.Add(new FeedPreviewRow($"{package.Name} {package.Version}",
                    $"{package.Id} · {package.Kind} · {package.File}"
                    + (package.Architectures.Count > 0 ? " · " + string.Join(", ", package.Architectures) : "")));
            foreach (var problem in inspection.Problems)
                Problems.Add(new FeedPreviewRow(problem.File, problem.Reason));
            Warning = inspection.Warning ?? "";
            Summary = CoreTools.Translate("{0} packages found, {1} files skipped.", Packages.Count, Problems.Count);
#endif
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
