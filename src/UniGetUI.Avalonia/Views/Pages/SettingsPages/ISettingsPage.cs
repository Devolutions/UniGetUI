namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public interface ISettingsPage
{
    bool CanGoBack { get; }
    string ShortTitle { get; }

    event EventHandler? RestartRequired;
    event EventHandler<Type>? NavigationRequested;
}
