using CommunityToolkit.Mvvm.ComponentModel;
using UniGetUI.Avalonia.ViewModels;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class InternetViewModel : ViewModelBase
{
    [ObservableProperty] private bool _isProxyEnabled;
    [ObservableProperty] private bool _isProxyAuthEnabled;

    public static void ApplyProxyToProcess()
    {
        var proxyUri = CoreSettings.GetProxyUrl();
        if (proxyUri is null || !CoreSettings.Get(CoreSettings.K.EnableProxy))
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", "", EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("HTTPS_PROXY", "", EnvironmentVariableTarget.Process);
            return;
        }
        string content;
        if (!CoreSettings.Get(CoreSettings.K.EnableProxyAuth))
        {
            content = proxyUri.ToString();
        }
        else
        {
            var creds = CoreSettings.GetProxyCredentials();
            content = creds is not null
                ? $"{proxyUri.Scheme}://{creds.UserName}:{creds.Password}@{proxyUri.Host}:{proxyUri.Port}"
                : proxyUri.ToString();
        }
        Environment.SetEnvironmentVariable("HTTP_PROXY", content, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("HTTPS_PROXY", content, EnvironmentVariableTarget.Process);
    }
}
