using Octokit;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SecureSettings;
using UniGetUI.Core.Tools;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.Infrastructure;

internal class GitHubAuthService
{
    private readonly string _gitHubClientId = Secrets.GetGitHubClientId();
    private readonly GitHubClient _client;

    public static event EventHandler<EventArgs>? AuthStatusChanged;

    /// <summary>
    /// Fired when the device flow has started. Provides the user code and verification URI
    /// that must be shown to the user so they can authorize the app at GitHub.
    /// </summary>
    public static event EventHandler<(string UserCode, string VerificationUri)>? DeviceFlowStarted;

    public GitHubAuthService()
    {
        _client = new GitHubClient(new ProductHeaderValue("UniGetUI", CoreData.VersionName));
    }

    public GitHubClient? CreateGitHubClient()
    {
        var token = SecureGHTokenManager.GetToken();
        if (string.IsNullOrEmpty(token))
            return null;

        return new GitHubClient(new ProductHeaderValue("UniGetUI", CoreData.VersionName))
        {
            Credentials = new Credentials(token),
        };
    }

    public async Task<bool> SignInAsync()
    {
        try
        {
            Logger.Info("Initiating GitHub sign-in using device flow...");

            var deviceFlow = await _client.Oauth.InitiateDeviceFlow(
                new OauthDeviceFlowRequest(_gitHubClientId)
                {
                    Scopes = { "read:user", "gist" },
                }, CancellationToken.None);

            // Open the verification page and notify the UI layer so it can show the user code.
            CoreTools.Launch(deviceFlow.VerificationUri);
            DeviceFlowStarted?.Invoke(this, (deviceFlow.UserCode, deviceFlow.VerificationUri));

            // Octokit handles polling with the correct interval until the user authorises or the code expires.
            var token = await _client.Oauth.CreateAccessTokenForDeviceFlow(_gitHubClientId, deviceFlow, CancellationToken.None);

            if (string.IsNullOrEmpty(token.AccessToken))
            {
                Logger.Error("Failed to obtain GitHub access token via device flow.");
                AuthStatusChanged?.Invoke(this, EventArgs.Empty);
                return false;
            }

            Logger.Info("GitHub device flow login successful. Storing access token.");
            SecureGHTokenManager.StoreToken(token.AccessToken);

            var userClient = new GitHubClient(new ProductHeaderValue("UniGetUI"))
            {
                Credentials = new Credentials(token.AccessToken),
            };
            var user = await userClient.User.Current();
            if (user is not null)
            {
                CoreSettings.SetValue(CoreSettings.K.GitHubUserLogin, user.Login);
                Logger.Info($"Logged in as GitHub user: {user.Login}");
            }

            AuthStatusChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Exception during GitHub device flow sign-in:");
            Logger.Error(ex);
            ClearAuthenticatedUserData();
            AuthStatusChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }
    }

    public void SignOut()
    {
        Logger.Info("Signing out from GitHub...");
        try { ClearAuthenticatedUserData(); }
        catch (Exception ex) { Logger.Error("Failed to log out:"); Logger.Error(ex); }
        AuthStatusChanged?.Invoke(this, EventArgs.Empty);
        Logger.Info("GitHub sign-out complete.");
    }

    private static void ClearAuthenticatedUserData()
    {
        CoreSettings.SetValue(CoreSettings.K.GitHubUserLogin, "");
        SecureGHTokenManager.DeleteToken();
    }

    public bool IsAuthenticated() => !string.IsNullOrEmpty(SecureGHTokenManager.GetToken());
}
