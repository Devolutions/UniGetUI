using UniGetUI.Extensions.Xcode.AppleAuth;

namespace UniGetUI.Extensions.Xcode;

/// <summary>The Apple ID UniGetUI downloads with, as last read or checked.</summary>
/// <param name="AppleId">The saved Apple ID, or null when none is saved (or its Keychain item has not been unlocked yet).</param>
/// <param name="Session">What Apple said about the session the last time it was checked, or null.</param>
public sealed record XcodeAccountStatus(string? AppleId, AppleSession? Session, bool HasSavedPassword = false, bool HasStoredItem = false)
{
    public static readonly XcodeAccountStatus None = new(null, null);

    public bool IsSignedIn => Session is not null;
    public bool HasSavedAccount => AppleId is not null || HasStoredItem;
}

/// <summary>What <see cref="XcodesService"/> does on this Mac; tests stand in for it.</summary>
internal interface IXcodeBackend
{
    Task<IReadOnlyList<InstalledXcode>> GetInstalledAsync(string? installDirectory, CancellationToken cancellationToken);

    /// <param name="refresh">Download the release list instead of using the saved one.</param>
    Task<IReadOnlyList<XcodeRelease>> GetReleasesAsync(bool refresh, string? installDirectory, CancellationToken cancellationToken);

    Task<XcodeInstallationResult> InstallAsync(XcodeRelease release, XcodeInstallOptions options, XcodesPromptHandler? prompts,
        Action<XcodesOutputLine>? output, CancellationToken cancellationToken);

    Task<string> DownloadAsync(XcodeRelease release, XcodesPromptHandler? prompts,
        Action<XcodesOutputLine>? output, CancellationToken cancellationToken, string? archiveDirectory = null);

    Task SelectAsync(string appPath, Action<XcodesOutputLine>? output, CancellationToken cancellationToken);

    XcodeAccountStatus GetAccount();

    Task<XcodeAccountStatus> RefreshAccountAsync(CancellationToken cancellationToken);

    Task<XcodeAccountStatus> SignInAsync(XcodesPromptHandler prompts, string? suggestedAppleId, CancellationToken cancellationToken);

    Task SignOutAsync(CancellationToken cancellationToken);
}
