using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.ViewModels.Pages.AboutPages;

public partial class ContributorEntry : ObservableObject
{
    public string Name { get; init; } = "";
    public Uri? GitHubUrl { get; init; }
    public bool HasGitHubProfile => GitHubUrl is not null;

    [ObservableProperty] private Bitmap? _profilePicture;

    private readonly Uri? _pictureUrl;

    public ContributorEntry(string name, Uri? gitHubUrl, Uri? pictureUrl)
    {
        Name = name;
        GitHubUrl = gitHubUrl;
        _pictureUrl = pictureUrl;
    }

    public async Task LoadPictureAsync()
    {
        if (_pictureUrl is null) return;
        try
        {
            using var http = new HttpClient(CoreTools.GenericHttpClientParameters);
            var bytes = await http.GetByteArrayAsync(_pictureUrl);
            using var ms = new MemoryStream(bytes);
            ProfilePicture = new Bitmap(ms);
        }
        catch { }
    }
}

public class ContributorsViewModel : ViewModelBase
{
    public string ThanksText { get; } = CoreTools.Translate(
        "WingetUI wouldn't have been possible without the help of the contributors. Thank you all 🥳");

    public ObservableCollection<ContributorEntry> ContributorList { get; } = [];

    public ContributorsViewModel()
    {
        foreach (string contributor in ContributorsData.Contributors)
        {
            var entry = new ContributorEntry(
                name: "@" + contributor,
                gitHubUrl: new Uri("https://github.com/" + contributor),
                pictureUrl: new Uri("https://github.com/" + contributor + ".png")
            );
            ContributorList.Add(entry);
            _ = entry.LoadPictureAsync();
        }
    }
}
