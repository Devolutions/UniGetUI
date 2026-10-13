using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using UniGetUI.Core.Logging;

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

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        try
        {
            base.OnGotFocus(e);
        }
        catch (Exception ex)
        {
            Logger.Warn("The built-in browser refused keyboard focus");
            Logger.Warn(ex);
        }
    }
}
