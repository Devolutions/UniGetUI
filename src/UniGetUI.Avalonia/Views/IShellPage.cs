namespace UniGetUI.Avalonia.Views;

internal interface IShellPage
{
    string Title { get; }

    string Subtitle { get; }

    bool SupportsSearch { get; }

    string SearchPlaceholder { get; }

    void UpdateSearchQuery(string query);
}
