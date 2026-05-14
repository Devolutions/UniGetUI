using Avalonia.Controls;
using Avalonia.Platform;

namespace UniGetUI.Avalonia.Views.Controls;

public sealed class UniGetUiWebView : NativeWebView
{
    public UniGetUiWebView()
    {
        EnvironmentRequested += (_, args) =>
        {
            if (args is WindowsWebView2EnvironmentRequestedEventArgs winArgs)
                winArgs.UserDataFolder = App.WebViewUserDataFolder;
        };
    }
}
