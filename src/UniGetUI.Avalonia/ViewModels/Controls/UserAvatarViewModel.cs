using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Interface;
using MvvmRelayCommand = CommunityToolkit.Mvvm.Input.RelayCommand;

namespace UniGetUI.Avalonia.ViewModels.Controls;

public class UserAvatarViewModel : ViewModelBase, IDisposable
{
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly CancellationToken _lifetimeToken;
    private int _isDisposed;
    private long _refreshGeneration;

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
        _lifetimeToken = _lifetimeCancellation.Token;
        LoginCommand = new AsyncRelayCommand(LoginAsync);
        LogoutCommand = new MvvmRelayCommand(Logout);
        MoreDetailsCommand = new MvvmRelayCommand(() => CoreTools.Launch("https://devolutions.net/unigetui"));
        GitHubAuthService.AuthStatusChanged += OnAuthStatusChanged;
        _ = RefreshAsync();
    }

    private bool IsDisposed => Volatile.Read(ref _isDisposed) != 0;

    private bool CanApplyRefresh(long generation) =>
        !IsDisposed
        && !_lifetimeToken.IsCancellationRequested
        && generation == Volatile.Read(ref _refreshGeneration);

    private void OnAuthStatusChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed)
            _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (IsDisposed) return;

        long generation = Interlocked.Increment(ref _refreshGeneration);
        var service = new GitHubAuthService();
        bool authenticated = GitHubAuthService.IsAuthenticated();

        string displayName = "";
        Bitmap? bitmap = null;

        if (authenticated)
        {
            try
            {
                using var client = GitHubAuthService.CreateGitHubClient();
                if (client is not null)
                {
                    GitHubUser user = await client.GetCurrentUserAsync();
                    if (!CanApplyRefresh(generation)) return;

                    displayName = string.IsNullOrEmpty(user.Name)
                        ? $"@{user.Login}"
                        : $"{user.Name} (@{user.Login})";

                    if (!string.IsNullOrEmpty(user.AvatarUrl))
                    {
                        using var http = new HttpClient(CoreTools.GenericHttpClientParameters);
                        byte[] bytes = await http.GetByteArrayAsync(user.AvatarUrl, _lifetimeToken);
                        if (!CanApplyRefresh(generation)) return;

                        using var ms = new MemoryStream(bytes);
                        bitmap = new Bitmap(ms);
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                {
                    Logger.Warn("UserAvatarViewModel: failed to fetch GitHub user info");
                    Logger.Warn(ex);
                }
                authenticated = false;
            }
        }

        if (!CanApplyRefresh(generation))
        {
            bitmap?.Dispose();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!CanApplyRefresh(generation)) return;

            IsAuthenticated = authenticated;
            UserDisplayName = displayName;
            AvatarBitmap = bitmap;
            bitmap = null;
        });

        // A queued UI update can become obsolete while it is waiting to run.
        bitmap?.Dispose();
    }

    private async Task LoginAsync()
    {
        try
        {
            await new GitHubAuthService().SignInAsync();
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                Logger.Error("UserAvatarViewModel: login failed");
                Logger.Error(ex);
            }
        }
    }

    private void Logout()
    {
        try { new GitHubAuthService().SignOut(); }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                Logger.Error("UserAvatarViewModel: logout failed");
                Logger.Error(ex);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;

        GitHubAuthService.AuthStatusChanged -= OnAuthStatusChanged;
        // In-flight HTTP work can still observe this token. Cancelling is sufficient;
        // disposing the source here could race with that work.
        _lifetimeCancellation.Cancel();
    }
}
