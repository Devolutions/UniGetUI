using UniGetUI.PackageEngine.Managers.InstallerFeeds;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

internal static class PkgIndexApp
{
    public static FeedIndexApp? From(AppBundleInfo? app) =>
        app is null ? null : new() { Id = app.BundleId, Path = app.Path, Version = app.Version };

    // Only an absolute .app path is kept: it is read to detect the app, never written to.
    public static AppBundleInfo? ToInfo(FeedIndexApp? app) =>
        app is not null && !string.IsNullOrWhiteSpace(app.Id) && app.Path is { Length: > 1 } path && path.StartsWith('/')
            && path.TrimEnd('/').EndsWith(".app", StringComparison.OrdinalIgnoreCase) && !path.Split('/').Contains("..")
            ? new AppBundleInfo(app.Id.Trim(), path.TrimEnd('/'), string.IsNullOrWhiteSpace(app.Version) ? null : app.Version.Trim())
            : null;
}
