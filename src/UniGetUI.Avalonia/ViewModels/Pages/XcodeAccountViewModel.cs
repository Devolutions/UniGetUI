using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Extensions.Xcode;

namespace UniGetUI.Avalonia.ViewModels.Pages;

/// <summary>
/// The Apple ID UniGetUI downloads Xcode with. Shared by the Xcode page and its settings, including while the
/// extension is turned off.
/// </summary>
public partial class XcodeAccountViewModel : ViewModelBase
{
    private readonly XcodesService _xcodes;
    private XcodeAccountStatus _account = XcodeAccountStatus.None;

    public XcodeAccountViewModel(XcodesService xcodes)
    {
        _xcodes = xcodes;
        RefreshCore();
    }

    /// <summary>Answers Apple's sign-in questions; tests replace it.</summary>
    internal Func<XcodesPrompt, CancellationToken, Task<string?>> Ask { get; set; } = XcodePromptDialog.AskAsync;

    public InfoBarViewModel Notice { get; } = new();
    public string? Username => _account.AppleId;
    public bool HasAccount => _account.HasSavedAccount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignOut), nameof(CanSignIn), nameof(CanVerify))]
    [NotifyCanExecuteChangedFor(nameof(SignOutCommand), nameof(RefreshCommand), nameof(SignInCommand),
        nameof(UseDifferentAppleIdCommand), nameof(VerifyCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _description = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string _warning = "";

    public bool HasWarning => Warning.Length > 0;
    public bool CanSignOut => !IsBusy && HasAccount;
    public bool CanSignIn => !IsBusy;
    public bool CanVerify => !IsBusy && HasAccount;

    private bool CanRefresh() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void Refresh()
    {
        if (IsBusy) return;
        RefreshCore();
    }

    private void RefreshCore()
    {
        try
        {
            _account = _xcodes.GetAccount();
            Warning = "";
        }
        catch (Exception ex) when (ex is XcodesException or IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Could not read the saved Apple ID: {ex.Message}");
            _account = XcodeAccountStatus.None;
            Warning = CoreTools.Translate("Could not read the saved Apple ID: {0}", ex.Message);
        }
        Describe();
    }

    private void Describe()
    {
        (Status, Description) = _account switch
        {
            { IsSignedIn: true, Session: var session } => (
                CoreTools.Translate("Signed in as {0}", session?.FullName is { Length: > 0 } name ? $"{name} ({_account.AppleId})" : _account.AppleId!),
                CoreTools.Translate("Downloads use this Apple ID. To use another one, choose Use a different Apple ID.")),
            { AppleId: { } appleId } => (
                CoreTools.Translate("Saved Apple ID: {0}", appleId),
                _account.HasSavedPassword
                    ? CoreTools.Translate("UniGetUI signs in with this Apple ID when a download needs it. Verify checks the session now.")
                    : CoreTools.Translate("UniGetUI asks for this Apple ID's password when a download needs it.")),
            { HasStoredItem: true } => (
                CoreTools.Translate("Saved Apple ID in Keychain"),
                CoreTools.Translate("UniGetUI unlocks your saved Apple ID from the login keychain only when a download or verification needs it.")),
            _ => (
                CoreTools.Translate("Not signed in to Apple"),
                CoreTools.Translate("Sign in with an Apple ID to download Xcode. UniGetUI keeps the password in its own item of your login keychain.")
                + " " + CoreTools.Translate("Accounts saved by other apps or command-line tools are not used.")
                + " " + CoreTools.Translate("Accounts that require security keys or federated sign-in are not supported yet.")),
        };
        OnPropertyChanged(nameof(Username));
        OnPropertyChanged(nameof(HasAccount));
        OnPropertyChanged(nameof(CanSignOut));
        OnPropertyChanged(nameof(CanVerify));
        SignOutCommand.NotifyCanExecuteChanged();
        VerifyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The Apple ID right now, read again so an operation sees the latest one.</summary>
    internal XcodeAccountStatus RefreshForOperation()
    {
        RefreshCore();
        return _account;
    }

    // Both page operations and account commands acquire this on the UI thread, before their first await.
    internal bool TryBeginOperation(bool refresh = true)
    {
        if (IsBusy) return false;
        IsBusy = true;
        Notice.IsOpen = false;
        if (refresh)
            RefreshCore();
        return true;
    }

    internal void EndOperation(bool refresh = true)
    {
        if (refresh)
            RefreshCore();
        IsBusy = false;
    }

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private Task SignIn() => SignInCoreAsync(Username);

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private Task UseDifferentAppleId() => SignInCoreAsync(null);

    private async Task SignInCoreAsync(string? suggestedAppleId)
    {
        if (!TryBeginOperation()) return;
        try
        {
            _account = await _xcodes.SignInAsync((prompt, token) => Ask(prompt, token), suggestedAppleId);
            ShowNotice(CoreTools.Translate("Signed in to Apple"),
                CoreTools.Translate("Xcode downloads now use {0}.", _account.AppleId ?? ""), InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
        }
        catch (XcodesException ex)
        {
            Logger.Warn($"Could not sign in to Apple: {ex.Message}");
            ShowNotice(CoreTools.Translate("Could not sign in to Apple"), ex.Message, InfoBarSeverity.Error, ex.IsAccessFailure);
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanVerify))]
    private async Task Verify()
    {
        if (!TryBeginOperation()) return;
        try
        {
            _account = await _xcodes.RefreshAccountAsync();
            if (_account.IsSignedIn)
                ShowNotice(CoreTools.Translate("Apple session is valid"),
                    CoreTools.Translate("Xcode downloads use {0}.", _account.AppleId ?? ""), InfoBarSeverity.Success);
            else
                ShowNotice(CoreTools.Translate("Apple session expired"),
                    CoreTools.Translate("UniGetUI signs in again when a download needs it, or choose Sign in now."),
                    InfoBarSeverity.Informational);
        }
        catch (OperationCanceledException)
        {
        }
        catch (XcodesException ex)
        {
            Logger.Warn($"Could not check the Apple session: {ex.Message}");
            ShowNotice(CoreTools.Translate("Could not check the Apple session"), ex.Message, InfoBarSeverity.Error, ex.IsAccessFailure);
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSignOut))]
    private async Task SignOut()
    {
        string? displayed = Username;
        if (!CanSignOut || !TryBeginOperation()) return;
        try
        {
            if (!string.Equals(displayed, Username, StringComparison.Ordinal))
            {
                ShowNotice(CoreTools.Translate("Apple ID changed"),
                    CoreTools.Translate("Review the current Apple ID before signing out."), InfoBarSeverity.Warning);
                return;
            }

            await _xcodes.SignOutAsync();
            ShowNotice(CoreTools.Translate("Signed out of Apple"),
                CoreTools.Translate("UniGetUI removed the Apple ID, its password and its session. Installed versions and downloads are kept."),
                InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is XcodesException or IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Could not sign out of Apple: {ex.Message}");
            ShowNotice(CoreTools.Translate("Could not sign out"), ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            EndOperation();
        }
    }

    private void ShowNotice(string title, string message, InfoBarSeverity severity, bool offerDeveloperAccount = false)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.ActionButtonText = offerDeveloperAccount ? CoreTools.Translate("Apple Developer account") : "";
        Notice.ActionButtonCommand = offerDeveloperAccount ? OpenDeveloperAccountCommand : null;
        Notice.IsOpen = true;
    }

    [RelayCommand]
    private static void OpenDeveloperAccount() => CoreTools.Launch("https://developer.apple.com/account/");
}
