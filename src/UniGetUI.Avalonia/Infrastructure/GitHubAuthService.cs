using Octokit;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SecureSettings;
using UniGetUI.Core.Tools;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.Infrastructure;

internal sealed class GitHubAuthService
{
    private const string MissingClientId = "CLIENT_ID_UNSET";
    private const string MissingClientSecret = "CLIENT_SECRET_UNSET";
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

    public static GitHubClient? CreateGitHubClient()
    {
        var token = SecureGHTokenManager.GetToken();
        if (string.IsNullOrEmpty(token))
            return null;

        return new GitHubClient(new ProductHeaderValue("UniGetUI", CoreData.VersionName))
        {
            Credentials = new Credentials(token),
        };
    }

    private GHAuthApiRunner? _loginBackend;
    private string? _codeFromApi;
    private bool _loginWasCancelled;

    public async Task<bool> SignInAsync()
    {
        try
        {
            if (!HasConfiguredOAuthClient())
            {
                Logger.Error("GitHub sign-in is not configured for this build. Missing OAuth client ID or client secret.");
                AuthStatusChanged?.Invoke(this, EventArgs.Empty);
                return false;
            }

            Logger.Info("Initiating GitHub sign-in process using loopback redirect...");

            var request = new OauthLoginRequest(_gitHubClientId)
            {
                Scopes = { "read:user", "gist" },
                RedirectUri = new Uri(RedirectUri),
            };

            var oauthLoginUrl = _client.Oauth.GetGitHubLoginUrl(request);

            _codeFromApi = null;
            _loginWasCancelled = false;
            await StopLoginBackend();
            _loginBackend = new GHAuthApiRunner();
            _loginBackend.OnLogin += BackgroundApiOnOnLogin;
            _loginBackend.OnCancelled += BackgroundApiOnCancelled;
            await _loginBackend.Start();

            CoreTools.Launch(oauthLoginUrl.ToString());

            DateTime timeoutAt = DateTime.UtcNow.Add(LoginTimeout);
            while (_codeFromApi is null && !_loginWasCancelled && DateTime.UtcNow < timeoutAt)
                await Task.Delay(100);

            if (_loginWasCancelled)
            {
                Logger.Warn("GitHub sign-in was cancelled by the user.");
                AuthStatusChanged?.Invoke(this, EventArgs.Empty);
                return false;
            }

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
            await StopLoginBackend();
        }
    }

    private void BackgroundApiOnOnLogin(object? sender, string code)
    {
        _codeFromApi = code;
    }

    private void BackgroundApiOnCancelled(object? sender, string error)
    {
        _loginWasCancelled = true;
    }

    private async Task StopLoginBackend()
    {
        if (_loginBackend is null) return;
        try
        {
            _loginBackend.OnLogin -= BackgroundApiOnOnLogin;
            _loginBackend.OnCancelled -= BackgroundApiOnCancelled;
            await _loginBackend.Stop();
            _loginBackend.Dispose();
        }
        catch (Exception ex) { Logger.Warn(ex); }
        finally { _loginBackend = null; }
    }

    private bool HasConfiguredOAuthClient()
    {
        return !string.IsNullOrWhiteSpace(_gitHubClientId)
            && !string.IsNullOrWhiteSpace(_gitHubClientSecret)
            && !string.Equals(_gitHubClientId, MissingClientId, StringComparison.Ordinal)
            && !string.Equals(_gitHubClientSecret, MissingClientSecret, StringComparison.Ordinal);
    }

    private async Task<bool> CompleteSignInAsync(string code)
    {
        try
        {
            var tokenRequest = new OauthTokenRequest(_gitHubClientId, _gitHubClientSecret, code)
            {
                RedirectUri = new Uri(RedirectUri), // The same redirect_uri must be sent
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

    public static bool IsAuthenticated() => !string.IsNullOrEmpty(SecureGHTokenManager.GetToken());
}
