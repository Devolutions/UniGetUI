using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.ViewModels.Pages.AboutPages;

public partial class ThirdPartyComponentItem : ViewModelBase
{
    private readonly ThirdPartyComponent _component;

    public ThirdPartyComponentItem(ThirdPartyComponent component)
    {
        _component = component;
        SourceUrl = Uri.TryCreate(component.Source, UriKind.Absolute, out Uri? url)
            && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
                ? url
                : null;
    }

    public string Name => _component.Name;
    public string Version => _component.Version;
    public string License => string.IsNullOrWhiteSpace(_component.License) ? CoreTools.Translate("Unknown") : _component.License;
    public Uri? SourceUrl { get; }
    public bool HasSource => SourceUrl is not null;
    public string ToggleText => IsExpanded ? CoreTools.Translate("Hide license") : CoreTools.Translate("Show license");
    public string? ExpandedText => IsExpanded ? BuildLicenseText() : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleText))]
    [NotifyPropertyChangedFor(nameof(ExpandedText))]
    private bool _isExpanded;

    [RelayCommand]
    private void ToggleLicense() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void OpenSource()
    {
        if (SourceUrl is not null)
            CoreTools.Launch(SourceUrl.ToString());
    }

    private string BuildLicenseText()
    {
        StringBuilder text = new();
        if (!string.IsNullOrWhiteSpace(_component.Copyright))
            text.AppendLine(_component.Copyright.Trim()).AppendLine();

        text.AppendLine(string.IsNullOrWhiteSpace(_component.LicenseText)
            ? CoreTools.Translate("The license text of this component is in the third-party notices file.")
            : _component.LicenseText.Trim());

        if (!string.IsNullOrWhiteSpace(_component.Notice?.Text))
            text.AppendLine().AppendLine(_component.Notice.Text.Trim());

        return text.ToString().TrimEnd();
    }
}

public partial class ThirdPartyLicensesViewModel : ViewModelBase
{
    public string LicensesIntro { get; } = CoreTools.Translate(
        "UniGetUI uses the following libraries. Without them, UniGetUI wouldn't have been possible.");

    public ObservableCollection<ThirdPartyComponentItem> Licenses { get; } = [];

    public bool HasLicenses => Licenses.Count > 0;
    public bool CanOpenNoticesFile { get; } = File.Exists(ThirdPartyNotices.TextFilePath);

    public string ComponentCountText => CoreTools.Translate("{0} third-party components", Licenses.Count);

    public string MissingNoticesText { get; } = CoreTools.Translate(
        "The third-party notices are not included in this build of UniGetUI.");

    public ThirdPartyLicensesViewModel()
        : this(ThirdPartyNotices.Load()) { }

    public ThirdPartyLicensesViewModel(IEnumerable<ThirdPartyComponent> components)
    {
        foreach (ThirdPartyComponent component in components)
            Licenses.Add(new ThirdPartyComponentItem(component));
    }

    [RelayCommand]
    private void OpenNoticesFile()
    {
        if (CanOpenNoticesFile)
            CoreTools.Launch(ThirdPartyNotices.TextFilePath);
    }
}
