using System.Globalization;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Extensions.Xcode;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>How the Xcode page looks for updates and installs, as chosen under Settings &gt; Extensions.</summary>
internal static class XcodePreferences
{
    /// <summary>The choices offered for automatic checks, in minutes; zero means only on request.</summary>
    public static readonly int[] CheckIntervals = [0, 240, 1440, 10080];

    public static int CheckIntervalMinutes
    {
        get => int.TryParse(Settings.GetValue(Settings.K.XcodeCheckInterval), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int minutes) && CheckIntervals.Contains(minutes) ? minutes : 0;
        set => Settings.SetValue(Settings.K.XcodeCheckInterval,
            value > 0 ? value.ToString(CultureInfo.InvariantCulture) : "");
    }

    /// <summary>Whether automatic checks may install a compatible Xcode update.</summary>
    public static bool AutoInstallUpdates
    {
        get => Settings.Get(Settings.K.XcodeAutoInstallUpdates);
        set => Settings.Set(Settings.K.XcodeAutoInstallUpdates, value);
    }

    /// <summary>Whether betas and release candidates are listed, and offered as the update.</summary>
    public static bool IncludePrereleases
    {
        get => Settings.Get(Settings.K.XcodeIncludePrereleases);
        set => Settings.Set(Settings.K.XcodeIncludePrereleases, value);
    }

    /// <summary>Whether a finished installation becomes the Xcode that command line tools use.</summary>
    public static bool SelectAfterInstall
    {
        get => Settings.Get(Settings.K.XcodeSelectAfterInstall);
        set => Settings.Set(Settings.K.XcodeSelectAfterInstall, value);
    }

    /// <summary>Whether an installation skips the steps that need an administrator's password.</summary>
    public static bool SkipAdministratorSteps
    {
        get => Settings.Get(Settings.K.XcodeSkipAdminSteps);
        set => Settings.Set(Settings.K.XcodeSkipAdminSteps, value);
    }

    /// <summary>Whether the downloaded archive is deleted, rather than kept for a later reinstall, once it is unpacked.</summary>
    public static bool DeleteArchive
    {
        get => Settings.Get(Settings.K.XcodeDeleteArchive);
        set => Settings.Set(Settings.K.XcodeDeleteArchive, value);
    }

    public static string? InstallDirectory
        => Settings.GetValue(Settings.K.XcodeInstallDirectory) is { Length: > 0 } directory ? directory : null;

    public static bool TrySetInstallDirectory(string? directory)
        => Settings.TrySetValue(Settings.K.XcodeInstallDirectory, directory ?? "");

    public static string? ArchiveDirectory
        => Settings.GetValue(Settings.K.XcodeArchiveDirectory) is { Length: > 0 } directory ? directory : null;

    public static bool TrySetArchiveDirectory(string? directory)
        => Settings.TrySetValue(Settings.K.XcodeArchiveDirectory, directory ?? "");

    public static bool BrowserInstalledOnly
    {
        get => Settings.Get(Settings.K.XcodeBrowserInstalledOnly);
        set => Settings.Set(Settings.K.XcodeBrowserInstalledOnly, value);
    }

    public static bool BrowserGrouped
    {
        get => Settings.Get(Settings.K.XcodeBrowserGrouped);
        set => Settings.Set(Settings.K.XcodeBrowserGrouped, value);
    }

    public static HashSet<string> BrowserCollapsedGroups
        => [.. Settings.GetValue(Settings.K.XcodeBrowserCollapsedGroups)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)];

    public static void SaveBrowserCollapsedGroups(IEnumerable<string> groups)
        => Settings.SetValue(Settings.K.XcodeBrowserCollapsedGroups,
            string.Join(",", groups.Order(StringComparer.Ordinal)));

    public static int BrowserReleaseFilter
    {
        get => ParseReleaseFilter(Settings.GetValue(Settings.K.XcodeBrowserReleaseFilter));
        set => Settings.SetValue(Settings.K.XcodeBrowserReleaseFilter, FormatReleaseFilter(value));
    }

    public static int BrowserArchitectureFilter
    {
        get => ParseArchitectureFilter(Settings.GetValue(Settings.K.XcodeBrowserArchitectureFilter));
        set => Settings.SetValue(Settings.K.XcodeBrowserArchitectureFilter, FormatArchitectureFilter(value));
    }

    public static int RuntimePlatformFilter
    {
        get => Settings.GetValue(Settings.K.XcodeRuntimePlatformFilter) switch
        {
            "ios" => 1,
            "watchos" => 2,
            "tvos" => 3,
            "visionos" => 4,
            _ => 0,
        };
        set => Settings.SetValue(Settings.K.XcodeRuntimePlatformFilter, value switch
        {
            1 => "ios",
            2 => "watchos",
            3 => "tvos",
            4 => "visionos",
            _ => "",
        });
    }

    public static int RuntimeSourceFilter
    {
        get => Settings.GetValue(Settings.K.XcodeRuntimeSourceFilter) switch
        {
            "installed" => 1,
            "apple-catalog" => 2,
            _ => 0,
        };
        set => Settings.SetValue(Settings.K.XcodeRuntimeSourceFilter, value switch
        {
            1 => "installed",
            2 => "apple-catalog",
            _ => "",
        });
    }

    public static int RuntimeReleaseFilter
    {
        get => ParseReleaseFilter(Settings.GetValue(Settings.K.XcodeRuntimeReleaseFilter));
        set => Settings.SetValue(Settings.K.XcodeRuntimeReleaseFilter, FormatReleaseFilter(value));
    }

    public static int RuntimeArchitectureFilter
    {
        get => ParseArchitectureFilter(Settings.GetValue(Settings.K.XcodeRuntimeArchitectureFilter));
        set => Settings.SetValue(Settings.K.XcodeRuntimeArchitectureFilter, FormatArchitectureFilter(value));
    }

    private static int ParseReleaseFilter(string value)
        => value switch
        {
            "release" => 1,
            "prerelease" => 2,
            _ => 0,
        };

    private static string FormatReleaseFilter(int value)
        => value switch
        {
            1 => "release",
            2 => "prerelease",
            _ => "",
        };

    private static int ParseArchitectureFilter(string value)
        => value switch
        {
            "apple-silicon" => 1,
            "universal" => 2,
            "intel" => 3,
            _ => 0,
        };

    private static string FormatArchitectureFilter(int value)
        => value switch
        {
            1 => "apple-silicon",
            2 => "universal",
            3 => "intel",
            _ => "",
        };

    public static XcodeInstallOptions InstallOptions
        => new(SelectAfterInstall, SkipAdministratorSteps, DeleteArchive, InstallDirectory, ArchiveDirectory);
}
