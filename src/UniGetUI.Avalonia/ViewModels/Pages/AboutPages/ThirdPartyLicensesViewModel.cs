using System.Collections.ObjectModel;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.ViewModels.Pages.AboutPages;

public class LibraryLicense
{
    public string Name { get; init; } = "";
    public string License { get; init; } = "";
    public Uri? LicenseURL { get; init; }
    public string HomepageText { get; init; } = "";
    public Uri? HomepageUrl { get; init; }
}

public class ThirdPartyLicensesViewModel : ViewModelBase
{
    public string LicensesIntro { get; } = CoreTools.Translate(
        "WingetUI Uses the following libraries. Without them, WingetUI wouldn't have been possible.");

    public ObservableCollection<LibraryLicense> Licenses { get; } = [];

    public ThirdPartyLicensesViewModel()
    {
        foreach (string license in LicenseData.LicenseNames.Keys)
        {
            Licenses.Add(new LibraryLicense
            {
                Name = license,
                License = LicenseData.LicenseNames[license],
                LicenseURL = LicenseData.LicenseURLs[license],
                HomepageUrl = LicenseData.HomepageUrls[license],
                HomepageText = CoreTools.Translate("{0} homepage", license),
            });
        }
    }
}
