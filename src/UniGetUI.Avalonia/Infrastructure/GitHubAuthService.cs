using Octokit;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SecureSettings;
using UniGetUI.Core.Tools;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.Infrastructure;

internal class GitHubAuthService
{
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(2);
    private readonly string _gitHubClientId = Secrets.GetGitHubClientId();
    private readonly string _gitHubClientSecret = Secrets.GetGitHubClientSecret();
    private const string RedirectUri = "http://127.0.0.1:58642/";
    private readonly GitHubClient _client;

    public static event EventHandler<EventArgs>? AuthStatusChanged;

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

    private GitHubAuthApiRunner? _loginBackend;
    private string? _codeFromApi;

    public async Task<bool> SignInAsync()
    {
        try
        {
            Logger.Info("Initiating GitHub sign-in process using loopback redirect...");
            var request = new OauthLoginRequest(_gitHubClientId)
            {
                Scopes = { "read:user", "gist" },
                RedirectUri = new Uri(RedirectUri),
            };
            var oauthLoginUrl = _client.Oauth.GetGitHubLoginUrl(request);

            _codeFromApi = null;
            if (_loginBackend is not null)
            {
                try { await _loginBackend.Stop(); _loginBackend.Dispose(); _loginBackend = null; }
                catch (Exception ex) { Logger.Warn(ex); }
            }

            _loginBackend = new GitHubAuthApiRunner();
            _loginBackend.OnLogin += BackendOnLogin;
            await _loginBackend.Start();

            CoreTools.Launch(oauthLoginUrl.ToString());

            var timeoutAt = DateTime.UtcNow.Add(LoginTimeout);
            while (_codeFromApi is null && DateTime.UtcNow < timeoutAt)
                await Task.Delay(100);

            if (string.IsNullOrEmpty(_codeFromApi))
            {
                Logger.Error("GitHub sign-in timed out before the loopback callback was received.");
                AuthStatusChanged?.Invoke(this, EventArgs.Empty);
                return false;
            }

            return await CompleteSignInAsync(_codeFromApi);
        }
        catch (Exception ex)
        {
            Logger.Error("Exception during GitHub sign-in process:");
            Logger.Error(ex);
            ClearAuthenticatedUserData();
            AuthStatusChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }
        finally
        {
            if (_loginBackend is not null)
            {
                try
                {
                    _loginBackend.OnLogin -= BackendOnLogin;
                    await _loginBackend.Stop();
                    _loginBackend.Dispose();
                }
                catch (Exception ex) { Logger.Warn(ex); }
                finally { _loginBackend = null; }
            }
        }
    }

    private void BackendOnLogin(object? sender, string code)
    {
        _codeFromApi = code;
    }

    private async Task<bool> CompleteSignInAsync(string code)
    {
        try
        {
            var tokenRequest = new OauthTokenRequest(_gitHubClientId, _gitHubClientSecret, code)
            {
                RedirectUri = new Uri(RedirectUri),
            };
            var token = await _client.Oauth.CreateAccessToken(tokenRequest);

            if (string.IsNullOrEmpty(token.AccessToken))
            {
                Logger.Error("Failed to obtain GitHub access token.");
                AuthStatusChanged?.Invoke(this, EventArgs.Empty);
                return false;
            }

            Logger.Info("GitHub login successful. Storing access token.");
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
            Logger.Error("Exception during GitHub token exchange:");
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
