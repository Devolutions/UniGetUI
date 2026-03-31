using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Octokit;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using MvvmRelayCommand = CommunityToolkit.Mvvm.Input.RelayCommand;

namespace UniGetUI.Avalonia.ViewModels.Controls;

public class UserAvatarViewModel : ViewModelBase
{
    private bool _isAuthenticated;
    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        private set => SetProperty(ref _isAuthenticated, value);
    }

    private string _userDisplayName = "";
    public string UserDisplayName
    {
        get => _userDisplayName;
        private set => SetProperty(ref _userDisplayName, value);
    }

    private Bitmap? _avatarBitmap;
    public Bitmap? AvatarBitmap
    {
        get => _avatarBitmap;
        private set => SetProperty(ref _avatarBitmap, value);
    }

    public IAsyncRelayCommand LoginCommand { get; }
    public IRelayCommand LogoutCommand { get; }
    public IRelayCommand MoreDetailsCommand { get; }

    public UserAvatarViewModel()
    {
        LoginCommand = new AsyncRelayCommand(LoginAsync);
        LogoutCommand = new MvvmRelayCommand(Logout);
        MoreDetailsCommand = new MvvmRelayCommand(() => CoreTools.Launch("https://devolutions.net/unigetui"));
        GitHubAuthService.AuthStatusChanged += (_, _) => _ = RefreshAsync();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var service = new GitHubAuthService();
        bool authenticated = service.IsAuthenticated();

        string displayName = "";
        Bitmap? bitmap = null;

        if (authenticated)
        {
            try
            {
                var client = service.CreateGitHubClient();
                if (client is not null)
                {
                    User user = await client.User.Current();
                    displayName = string.IsNullOrEmpty(user.Name)
                        ? $"@{user.Login}"
                        : $"{user.Name} (@{user.Login})";

                    if (!string.IsNullOrEmpty(user.AvatarUrl))
                    {
                        using var http = new HttpClient(CoreTools.GenericHttpClientParameters);
                        byte[] bytes = await http.GetByteArrayAsync(user.AvatarUrl);
                        using var ms = new MemoryStream(bytes);
                        bitmap = new Bitmap(ms);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("UserAvatarViewModel: failed to fetch GitHub user info");
                Logger.Warn(ex);
                authenticated = false;
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsAuthenticated = authenticated;
            UserDisplayName = displayName;
            AvatarBitmap = bitmap;
        });
    }

    private async Task LoginAsync()
    {
        try
        {
            await new GitHubAuthService().SignInAsync();
        }
        catch (Exception ex)
        {
            Logger.Error("UserAvatarViewModel: login failed");
            Logger.Error(ex);
        }
    }

    private void Logout()
    {
        try { new GitHubAuthService().SignOut(); }
        catch (Exception ex)
        {
            Logger.Error("UserAvatarViewModel: logout failed");
            Logger.Error(ex);
        }
    }
}
