using System.Globalization;
using System.Text;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// A single searchable destination inside the settings/managers tree.
/// </summary>
public sealed class SettingsSearchResult
{
    public required string Title { get; init; }      // translated section/setting name
    public required string PageTitle { get; init; }  // translated breadcrumb (containing page)
    public Type? PageType { get; init; }             // settings sub-page to open
    public IPackageManager? Manager { get; init; }   // or, a package manager to open
    public string? Anchor { get; init; }             // x:Name of the control to scroll to
}

/// <summary>
/// Hand-authored index that maps a typed query to a settings destination. Titles are the same
/// English source strings used by <see cref="CoreTools.Translate(string)"/> in the AXAML, so the
/// index matches in the user's language; the English keywords keep those terms searchable in any
/// locale.
/// </summary>
public static class SettingsSearchIndex
{
    private sealed record Entry(string Title, string[] Keywords, Type PageType, string? Anchor);

    // Order matters only as a tie-break for equally-ranked matches.
    private static readonly Entry[] Entries =
    [
        // ── General ──────────────────────────────────────────────────────────
        // ── General ──────────────────────────────────────────────────────────
        new("UniGetUI display language:", ["language", "lang", "locale", "translation", "idioma"], typeof(General), "LanguageSelector"),
        new("Update UniGetUI automatically", ["app update", "auto update", "self update"], typeof(General), "GeneralUpdaterCard"),
        new("Install prerelease versions of UniGetUI", ["beta", "prerelease", "preview"], typeof(General), "EnableUniGetUIBetaCard"),
        new("Show the release notes after UniGetUI is updated", ["release notes", "changelog"], typeof(General), "ReleaseNotesOnUpdateCard"),
        new("Manage telemetry settings", ["telemetry", "usage data", "analytics"], typeof(General), "GeneralTelemetryCard"),
        new("Hide my username from the logs", ["privacy", "redact", "username", "logs"], typeof(General), "GeneralPrivacyCard"),
        new("Import settings from a local file", ["import settings"], typeof(General), "GeneralImportCard"),
        new("Export settings to a local file", ["export settings"], typeof(General), "ExportSettingsCard"),
        new("Reset UniGetUI", ["reset", "factory reset", "reset settings"], typeof(General), "ResetSettingsCard"),
        new("General preferences", ["general"], typeof(General), null),

        // ── User interface ───────────────────────────────────────────────────
        new("Application theme:", ["appearance", "theme", "dark", "light", "mica"], typeof(Interface_P), "ThemeSelector"),
        new("UniGetUI startup page:", ["startup page", "default page", "home page", "landing page"], typeof(Interface_P), "StartupPageSelector"),
        new("Navigation menu:", ["sidebar", "nav menu", "navigation", "layout", "docked", "overlay"], typeof(Interface_P), "NavMenuModeSelector"),
        new("Automatically switch to software rendering when Windows has no hardware GPU", ["rendering", "gpu", "software rendering"], typeof(Interface_P), "InterfaceRenderingCard"),
        new("Close UniGetUI to the system tray", ["system tray", "background", "minimize to tray"], typeof(Interface_P), "DisableSystemTrayCard"),
        new("Manage UniGetUI autostart behaviour", ["autostart", "run at login", "startup"], typeof(Interface_P), "EditAutostartSettings"),
        new("Show package icons on package lists", ["package icons"], typeof(Interface_P), "InterfacePackageListsCard"),
        new("Show illustrations on package lists", ["illustrations", "package illustrations"], typeof(Interface_P), "PackageIllustrationsCard"),
        new("Clear the icon cache", ["icon cache", "clear cache", "cache size"], typeof(Interface_P), "ResetIconCache"),
        new("Select upgradable packages by default", ["select updates", "select upgradable"], typeof(Interface_P), "SelectUpgradableCard"),
        new("User interface preferences", ["interface", "ui"], typeof(Interface_P), null),

        // ── Notifications ────────────────────────────────────────────────────
        new("Enable UniGetUI notifications", ["notifications", "enable notifications"], typeof(Notifications), "NotificationsMainCard"),
        new("Show an in-app summary toast when a batch of operations finishes", ["summary toast", "batch summary"], typeof(Notifications), "SummaryToastCard"),
        new("Show a notification when there are available updates", ["update notification"], typeof(Notifications), "NotificationTypesCard"),
        new("Show a silent notification when an operation is running", ["progress notification", "silent notification"], typeof(Notifications), "ProgressNotificationCard"),
        new("Show a notification when an operation fails", ["error notification", "failure notification"], typeof(Notifications), "ErrorNotificationCard"),
        new("Show a notification when an operation finishes successfully", ["success notification"], typeof(Notifications), "SuccessNotificationCard"),
        new("Notification preferences", ["notifications"], typeof(Notifications), null),

        // ── Updates ──────────────────────────────────────────────────────────
        new("Check for package updates periodically", ["check for updates", "periodically"], typeof(Updates), "UpdatesCheckingCard"),
        new("Check for updates every:", ["update frequency", "update interval"], typeof(Updates), "UpdatesCheckIntervalSelector"),
        new("Install available updates automatically", ["automatic updates", "auto install updates"], typeof(Updates), "UpdatesAutomaticCard"),
        new("Do not automatically install updates when the network connection is metered", ["metered connection"], typeof(Updates), "AUPMeteredCard"),
        new("Do not automatically install updates when the device runs on battery", ["battery"], typeof(Updates), "AUPBatteryCard"),
        new("Do not automatically install updates when the battery saver is on", ["battery saver"], typeof(Updates), "AUPBatterySaverCard"),
        new("Minimum age for updates", ["minimum age", "update security"], typeof(Updates), "MinimumUpdateAgeSelector"),
        new("Custom minimum age (days)", ["custom age"], typeof(Updates), "MinimumUpdateAgeCustomInput"),
        new("Warn me when the installer URL host changes between the installed version and the new version (WinGet only)", ["installer host", "url host"], typeof(Updates), "InstallerHostWarningCard"),
        new("Package update preferences", ["updates"], typeof(Updates), null),

        // ── Operations ───────────────────────────────────────────────────────
        new("Choose how many operations should be performed in parallel", ["parallel", "concurrency"], typeof(Operations), "ParallelOperationCount"),
        new("Clear successful operations from the operation list after a 5 second delay", ["clear successful", "maintain installs"], typeof(Operations), "ClearSuccessfulOpsCard"),
        new("Try to kill the processes that refuse to close when requested to", ["kill processes"], typeof(Operations), "KillProcessesCard"),
        new("Ask to delete desktop shortcuts created during an install or upgrade.", ["desktop shortcuts", "shortcut remover"], typeof(Operations), "AskToDeleteNewDesktopShortcuts"),
        new("Package operation preferences", ["operations"], typeof(Operations), null),

        // ── Internet ─────────────────────────────────────────────────────────
        new("Connect the internet using a custom proxy", ["proxy"], typeof(Internet), "ProxyCard"),
        new("Proxy URL", ["proxy url"], typeof(Internet), "ProxyURLCard"),
        new("Authenticate to the proxy with a user and a password", ["proxy authentication", "proxy password"], typeof(Internet), "ProxyAuthCard"),
        new("Wait for the device to be connected to the internet before attempting to do tasks that require internet connectivity.", ["wait for internet", "connection"], typeof(Internet), "InternetOtherCard"),
        new("Internet connection settings", ["internet", "proxy"], typeof(Internet), null),

        // ── Backup ───────────────────────────────────────────────────────────
        new("Log in with GitHub", ["github login", "sign in"], typeof(Backup), "CloudBackupCard"),
        new("Periodically perform a cloud backup of the installed packages", ["cloud backup", "gist"], typeof(Backup), "CloudBackupCard"),
        new("Perform a cloud backup now", ["cloud backup now"], typeof(Backup), "CloudBackupNowCard"),
        new("Restore a backup from the cloud", ["restore cloud backup"], typeof(Backup), "RestoreCloudCard"),
        new("Periodically perform a local backup of the installed packages", ["local backup"], typeof(Backup), "LocalBackupCard"),
        new("Perform a local backup now", ["local backup now"], typeof(Backup), "BackupNowButton_LOCAL"),
        new("Change backup output directory", ["backup directory", "backup folder"], typeof(Backup), "BackupDirectoryCard"),
        new("Set a custom backup file name", ["backup file name"], typeof(Backup), "BackupFileNameCard"),
        new("Add a timestamp to the backup file names", ["backup timestamp"], typeof(Backup), "BackupTimestampCard"),
        new("Package backup", ["backup"], typeof(Backup), null),

        // ── Administrator ────────────────────────────────────────────────────
        new("Ask for administrator privileges once for each batch of operations", ["administrator", "admin rights", "elevation", "uac", "batch"], typeof(Administrator), "AdminElevationCard"),
        new("Ask only once for administrator privileges", ["admin once", "cache admin rights"], typeof(Administrator), "CacheAdminOnceCard"),
        new("Prohibit any kind of Elevation via UniGetUI Elevator or GSudo", ["prohibit elevation", "no elevation"], typeof(Administrator), "ProhibitElevationCard"),
        new("Allow custom command-line arguments", ["command line arguments", "cli arguments"], typeof(Administrator), "AdminRestrictionsOpsCard"),
        new("Ignore custom pre-install and post-install commands when importing packages from a bundle", ["pre-install commands", "post-install commands"], typeof(Administrator), "PrePostCommandCard"),
        new("Allow changing the paths for package manager executables", ["manager paths", "executable path"], typeof(Administrator), "AdminManagerPathsCard"),
        new("Allow importing custom command-line arguments when importing packages from a bundle", ["import cli arguments"], typeof(Administrator), "ImportCLIArgsCard"),
        new("Allow importing custom pre-install and post-install commands when importing packages from a bundle", ["import commands"], typeof(Administrator), "ImportPrePostCard"),
        new("Administrator rights and other dangerous settings", ["administrator", "dangerous"], typeof(Administrator), null),

        // ── Experimental ─────────────────────────────────────────────────────
        new("Show UniGetUI's version and build number on the titlebar.", ["version number", "titlebar", "build number"], typeof(Experimental), "ShowVersionNumberOnTitlebar"),
        new("Enable background api (UniGetUI Widgets and Sharing, port 7058)", ["api", "widgets", "sharing", "port"], typeof(Experimental), "BackgroundApiCard"),
        new("Disable the 1-minute timeout for package-related operations", ["timeout"], typeof(Experimental), "DisableTimeoutCard"),
        new("Use installed GSudo instead of UniGetUI Elevator", ["gsudo", "elevator"], typeof(Experimental), "ForceGSudoCard"),
        new("Use a custom icon and screenshot database URL", ["icon database", "screenshot database"], typeof(Experimental), "IconDatabaseURLCard"),
        new("Enable background CPU Usage optimizations (see Pull Request #3278)", ["cpu", "optimizations"], typeof(Experimental), "CpuOptimizationsCard"),
        new("Perform integrity checks at startup", ["integrity checks"], typeof(Experimental), "IntegrityChecksCard"),
        new("When batch installing packages from a bundle, install also packages that are already installed", ["bundle install installed"], typeof(Experimental), "InstallInstalledBundleCard"),
        new("Experimental settings and developer options", ["experimental", "developer"], typeof(Experimental), null),
    ];

    private const int NoWordMatch = 100;   // a single query word matched nothing
    private const int NoMatch = int.MaxValue;

    public static IReadOnlyList<SettingsSearchResult> Search(string query, int limit = 8)
    {
        var queryWords = Tokenize(query);
        if (queryWords.Count == 0) return [];

        var scored = new List<(int score, SettingsSearchResult result)>();

        foreach (var e in Entries)
        {
            int score = Rank(queryWords, CoreTools.Translate(e.Title), e.Title, e.Keywords);
            if (score < NoMatch)
                scored.Add((score, new SettingsSearchResult
                {
                    Title = CleanTitle(CoreTools.Translate(e.Title)),
                    PageTitle = PageTitle(e.PageType),
                    PageType = e.PageType,
                    Anchor = e.Anchor,
                }));
        }

        foreach (var manager in PEInterface.Managers)
        {
            int score = Rank(queryWords, manager.DisplayName, manager.DisplayName, [manager.Name]);
            if (score < NoMatch)
                scored.Add((score, new SettingsSearchResult
                {
                    Title = manager.DisplayName,
                    PageTitle = CoreTools.Translate("Package manager preferences"),
                    Manager = manager,
                }));
        }

        return scored
            .OrderBy(x => x.score)
            .ThenBy(x => x.result.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .Select(x => x.result)
            .ToList();
    }

    // Token-based match: every query word is scored independently against the entry's title words
    // (translated + English source) and keyword words. Entries where at least one query word matches
    // are ranked by (words that matched nothing) first, then match quality — so a superset query like
    // "application themes" still finds "Application theme", and plurals like "themes" match "theme".
    // Lower is better; NoMatch means nothing matched.
    private static int Rank(List<string> queryWords, string translatedTitle, string englishTitle, string[] keywords)
    {
        var titleWords = Tokenize(translatedTitle);
        foreach (var w in Tokenize(englishTitle))
            if (!titleWords.Contains(w)) titleWords.Add(w);

        var keywordWords = new List<string>();
        foreach (var kw in keywords)
            keywordWords.AddRange(Tokenize(kw));

        int unmatched = 0;
        int quality = 0;
        foreach (var word in queryWords)
        {
            int t = WordScore(word, titleWords);
            int k = WordScore(word, keywordWords);
            // Keyword hits rank a touch below title hits, but still count as matches.
            int best = Math.Min(t, k >= NoWordMatch ? NoWordMatch : k + 3);
            if (best >= NoWordMatch) unmatched++;
            else quality += best;
        }

        if (unmatched == queryWords.Count) return NoMatch;   // nothing matched at all
        return unmatched * 1000 + quality;
    }

    // Best match of one query word against a set of words. 0 exact, 1 prefix, 2 substring.
    private static int WordScore(string word, List<string> words)
    {
        // Treat a trailing-'s' plural as its singular so "themes" matches "theme".
        string singular = word.Length > 3 && word.EndsWith('s') ? word[..^1] : word;

        int best = NoWordMatch;
        foreach (var w in words)
        {
            best = Math.Min(best, ScorePair(word, w));
            if (singular != word) best = Math.Min(best, ScorePair(singular, w));
            if (best == 0) break;
        }
        return best;
    }

    private static int ScorePair(string word, string candidate)
    {
        if (candidate == word) return 0;
        if (candidate.StartsWith(word, StringComparison.Ordinal)) return 1;
        if (word.StartsWith(candidate, StringComparison.Ordinal) && candidate.Length >= 3) return 1;
        if (candidate.Contains(word, StringComparison.Ordinal)) return 2;
        return NoWordMatch;
    }

    // Some card labels end with a colon (e.g. "Application theme:"); keep it on the source string
    // (needed for the translation lookup) but drop it from what the dropdown shows. Handles the
    // full-width colon used by some locales too.
    private static string CleanTitle(string title) => title.TrimEnd(' ', '\t', ':', '：');

    // Split into normalized alphanumeric words (drops 1-char noise and punctuation).
    private static List<string> Tokenize(string value)
    {
        var result = new List<string>();
        string norm = Normalize(value);
        var sb = new StringBuilder();
        foreach (char c in norm)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0) Flush();
        }
        Flush();
        return result;

        void Flush()
        {
            if (sb.Length >= 2) result.Add(sb.ToString());
            sb.Clear();
        }
    }

    private static string PageTitle(Type pageType) => CoreTools.Translate(pageType.Name switch
    {
        nameof(General) => "General preferences",
        nameof(Interface_P) => "User interface preferences",
        nameof(Notifications) => "Notification preferences",
        nameof(Updates) => "Package update preferences",
        nameof(Operations) => "Package operation preferences",
        nameof(Internet) => "Internet connection settings",
        nameof(Backup) => "Package backup",
        nameof(Administrator) => "Administrator rights and other dangerous settings",
        nameof(Experimental) => "Experimental settings and developer options",
        _ => "UniGetUI Settings",
    });

    // Case- and diacritic-insensitive so "idioma"/"proxy"/"café" all match regardless of accents.
    private static string Normalize(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0) return "";

        var sb = new StringBuilder(trimmed.Length);
        foreach (char c in trimmed.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);

        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }
}
