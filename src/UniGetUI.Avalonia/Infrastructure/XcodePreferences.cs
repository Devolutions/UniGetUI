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

    /// <summary>Whether the downloaded archive is deleted, rather than moved to the Trash, once it is unpacked.</summary>
    public static bool DeleteArchive
    {
        get => Settings.Get(Settings.K.XcodeDeleteArchive);
        set => Settings.Set(Settings.K.XcodeDeleteArchive, value);
    }

    public static bool FastUnarchive
    {
        get => Settings.Get(Settings.K.XcodeFastUnarchive);
        set => Settings.Set(Settings.K.XcodeFastUnarchive, value);
    }

    public static XcodeInstallOptions InstallOptions
        => new(SelectAfterInstall, SkipAdministratorSteps, DeleteArchive, FastUnarchive);
}
