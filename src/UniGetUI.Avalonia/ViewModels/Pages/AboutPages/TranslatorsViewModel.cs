using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using UniGetUI.Core.Language;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.ViewModels.Pages.AboutPages;

public partial class TranslatorEntry : ObservableObject
{
    public string Name { get; init; } = "";
    public string Language { get; init; } = "";
    public Uri? GitHubUrl { get; init; }
    public bool HasGitHubProfile => GitHubUrl is not null;

    [ObservableProperty] private Bitmap? _profilePicture;

    private readonly Uri? _pictureUrl;

    public TranslatorEntry(string name, string language, Uri? gitHubUrl, Uri? pictureUrl)
    {
        Name = name;
        Language = language;
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

public class TranslatorsViewModel : ViewModelBase
{
    public string ThanksText { get; } = CoreTools.Translate(
        "UniGetUI has been translated to more than 40 languages thanks to the volunteer translators. Thank you 🤝");

    public ObservableCollection<TranslatorEntry> TranslatorList { get; } = [];

    public TranslatorsViewModel()
    {
        foreach (var person in LanguageData.TranslatorsList)
        {
            var entry = new TranslatorEntry(
                name: person.Name,
                language: person.Language,
                gitHubUrl: person.GitHubUrl,
                pictureUrl: person.ProfilePicture
            );
            TranslatorList.Add(entry);
            _ = entry.LoadPictureAsync();
        }
    }
}
