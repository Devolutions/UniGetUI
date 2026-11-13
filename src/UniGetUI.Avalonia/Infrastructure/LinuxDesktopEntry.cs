using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Platform;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Infrastructure;

internal static partial class LinuxDesktopEntry
{
    internal const string EntryFileName = "unigetui.desktop";
    internal const string IconFileName = "unigetui.png";
    internal const string GeneratedMarker = "X-UniGetUI-Generated=true";
    internal const string EnglishComment = "Graphical interface for your package managers";

    private const string MainGroup = "[Desktop Entry]";
    private static readonly Lock Gate = new();
    private static readonly string[] DefaultDataDirs = ["/usr/local/share", "/usr/share"];
    private static readonly Uri IconAsset = new("avares://UniGetUI/Assets/icon.png");
    private static readonly Uri TemplateAsset = new("avares://UniGetUI/Assets/unigetui.desktop.in");

    internal sealed record Assets(string Template, byte[] Icon, string? Locale, string? LocalizedComment);

    internal enum Outcome
    {
        Skipped,
        Failed,
        Disabled,
        SystemEntryExists,
        ReplacedBySystemEntry,
        NotGeneratedByUniGetUI,
        OwnedByAnotherCopy,
        RemovedByUser,
        UpToDate,
        Updated,
        Created,
    }

    public static void EnsureForCurrentProcess()
    {
        if (!OperatingSystem.IsLinux() || IsSandboxed(Environment.GetEnvironmentVariable))
            return;

        EnsureFor(
            Environment.ProcessPath,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            GetDataDirs(Environment.GetEnvironmentVariable("XDG_DATA_DIRS")));
    }

    internal static Outcome EnsureFor(string? executablePath, string dataHome, IEnumerable<string> dataDirs)
    {
        string? installDir = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrEmpty(executablePath) || string.IsNullOrEmpty(installDir)
            || AvaloniaAutoUpdater.LooksLikeDevBuild(Path.GetFileName(executablePath), installDir))
        {
            return Outcome.Skipped;
        }

        lock (Gate)
            return EnsureLocked(executablePath, dataHome, dataDirs);
    }

    private static Outcome EnsureLocked(string executablePath, string dataHome, IEnumerable<string> dataDirs)
    {
        try
        {
            if (!Path.IsPathRooted(dataHome))
                return Outcome.Skipped;

            using FileStream? processLock = AcquireProcessLock(dataHome);
            if (processLock is null)
            {
                Logger.Warn("Another UniGetUI process held the Linux desktop entry lock; the entry was not checked");
                return Outcome.Failed;
            }

            Outcome outcome = Ensure(
                executablePath,
                dataHome,
                dataDirs,
                LoadAssets(),
                enabled: !Settings.Get(Settings.K.DisableLinuxDesktopEntry));
            if (outcome is Outcome.Created or Outcome.Updated or Outcome.ReplacedBySystemEntry or Outcome.Disabled)
                Logger.Info($"Linux desktop entry: {outcome} for {executablePath}");
            else
                Logger.Debug($"Linux desktop entry left untouched: {outcome}");

            return outcome;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not create the Linux desktop entry: {ex.Message}");
            return Outcome.Failed;
        }
    }

    internal static TimeSpan ProcessLockTimeout { get; set; } = TimeSpan.FromSeconds(10);

    internal static string GetLockPath(string dataHome) =>
        Path.Combine(dataHome, "UniGetUI", "DesktopEntry", "desktop-entry.lock");

    private static FileStream? AcquireProcessLock(string dataHome)
    {
        string lockPath = GetLockPath(dataHome);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        DateTime deadline = DateTime.UtcNow + ProcessLockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    internal static Assets LoadAssets()
    {
        using StreamReader template = new(AssetLoader.Open(TemplateAsset), Encoding.UTF8);
        using Stream icon = AssetLoader.Open(IconAsset);
        using MemoryStream iconBytes = new();
        icon.CopyTo(iconBytes);

        return new Assets(
            template.ReadToEnd(),
            iconBytes.ToArray(),
            CoreTools.GetCurrentLocale(),
            CoreTools.Translate("Graphical interface for your package managers"));
    }

    internal static bool IsSandboxed(Func<string, string?> getEnvironmentVariable) =>
        !string.IsNullOrEmpty(getEnvironmentVariable("SNAP"))
        || !string.IsNullOrEmpty(getEnvironmentVariable("FLATPAK_ID"));

    internal static IReadOnlyList<string> GetDataDirs(string? xdgDataDirs)
    {
        IEnumerable<string> configured = (xdgDataDirs ?? "")
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Path.IsPathRooted);

        return configured.Concat(DefaultDataDirs).Distinct(StringComparer.Ordinal).ToList();
    }

    internal static string GetIconPath(string dataHome) =>
        Path.Combine(dataHome, "UniGetUI", "DesktopEntry", IconFileName);

    internal static Outcome Ensure(
        string executablePath,
        string dataHome,
        IEnumerable<string> dataDirs,
        Assets assets,
        bool enabled = true)
    {
        if (executablePath.Any(char.IsControl) || !Path.IsPathRooted(dataHome))
            return Outcome.Skipped;

        string entryPath = Path.Combine(dataHome, "applications", EntryFileName);
        string iconPath = GetIconPath(dataHome);
        string[]? existing = File.Exists(entryPath) ? File.ReadAllText(entryPath).Split('\n') : null;
        bool generated = existing is not null && IsGenerated(existing);

        if (!enabled)
        {
            if (File.Exists(iconPath))
                File.Delete(iconPath);
            if (generated)
                File.Delete(entryPath);
            return Outcome.Disabled;
        }

        if (dataDirs.Any(dir => File.Exists(Path.Combine(dir, "applications", EntryFileName))))
        {
            if (!generated)
                return Outcome.SystemEntryExists;

            if (File.Exists(iconPath))
                File.Delete(iconPath);
            File.Delete(entryPath);
            return Outcome.ReplacedBySystemEntry;
        }

        if (existing is null)
        {
            if (File.Exists(iconPath))
                return Outcome.RemovedByUser;

            WriteAtomically(entryPath, Encoding.UTF8.GetBytes(BuildEntry(assets, executablePath, iconPath)));
            WriteAtomically(iconPath, assets.Icon);
            return Outcome.Created;
        }

        if (!generated)
            return Outcome.NotGeneratedByUniGetUI;

        bool changed = false;
        string? target = GetMainGroupValue(existing, "TryExec") is { } tryExec ? UnescapeString(tryExec) : null;
        if (target != executablePath)
        {
            if (target is not null && File.Exists(target))
                return Outcome.OwnedByAnotherCopy;

            string[] moved = SetMainGroupValues(existing, ManagedValues(executablePath, iconPath));
            WriteAtomically(entryPath, Encoding.UTF8.GetBytes(string.Join('\n', moved)));
            changed = true;
        }

        if (!File.Exists(iconPath) || !File.ReadAllBytes(iconPath).AsSpan().SequenceEqual(assets.Icon))
        {
            WriteAtomically(iconPath, assets.Icon);
            changed = true;
        }

        return changed ? Outcome.Updated : Outcome.UpToDate;
    }

    internal static string BuildEntry(Assets assets, string executablePath, string iconPath)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal)
        {
            ["COMMENT"] = EscapeString(EnglishComment),
            ["EXEC"] = EscapeString(BuildExec(executablePath)),
            ["ICON"] = EscapeString(iconPath),
        };

        string body = TemplatePlaceholder().Replace(
            assets.Template.Replace("\r\n", "\n"),
            match => values[match.Groups[1].Value]);

        StringBuilder entry = new(body.TrimEnd('\n'));
        entry.Append('\n').Append("TryExec=").Append(EscapeString(executablePath)).Append('\n');
        if (assets.Locale is { } locale && LocaleKey().IsMatch(locale)
            && !string.IsNullOrWhiteSpace(assets.LocalizedComment) && assets.LocalizedComment != EnglishComment)
        {
            entry.Append($"Comment[{locale}]=").Append(EscapeString(assets.LocalizedComment)).Append('\n');
        }

        return entry.Append(GeneratedMarker).Append('\n').ToString();
    }

    // GLib (GNOME, Xfce, Cinnamon) rejects an entry whose program, taken from Exec before
    // field codes are expanded, does not exist, so the spec's "%%" for a literal '%' in the
    // program path hides the entry. Such a path is passed as an argument to /bin/sh instead,
    // where "%%" is expanded before the shell sees it. env would read a path containing '='
    // as a variable assignment.
    internal static string BuildExec(string executablePath) =>
        executablePath.Contains('%')
            ? $"/bin/sh -c {QuoteExecArgument("exec \"$0\"")} {QuoteExecArgument(executablePath)}"
            : QuoteExecArgument(executablePath);

    internal static string QuoteExecArgument(string argument)
    {
        StringBuilder quoted = new("\"");
        foreach (char c in argument)
        {
            if (c == '%')
            {
                quoted.Append("%%");
                continue;
            }

            if (c is '"' or '`' or '$' or '\\')
                quoted.Append('\\');
            quoted.Append(c);
        }

        return quoted.Append('"').ToString();
    }

    internal static string EscapeString(string value) =>
        value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r");

    internal static string UnescapeString(string value)
    {
        StringBuilder result = new();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 == value.Length)
            {
                result.Append(value[i]);
                continue;
            }

            char escaped = value[++i];
            result.Append(escaped switch
            {
                's' => ' ',
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                _ => escaped,
            });
        }

        return result.ToString();
    }

    private static Dictionary<string, string> ManagedValues(string executablePath, string iconPath) =>
        new(StringComparer.Ordinal)
        {
            ["Exec"] = EscapeString(BuildExec(executablePath)),
            ["TryExec"] = EscapeString(executablePath),
            ["Icon"] = EscapeString(iconPath),
        };

    private static bool IsGenerated(string[] lines) =>
        MainGroupRange(lines) is not null && lines.Any(line => line.Trim() == GeneratedMarker);

    private static (int Header, int End)? MainGroupRange(string[] lines)
    {
        int header = Array.FindIndex(lines, line => line.Trim() == MainGroup);
        if (header < 0)
            return null;

        int end = header + 1;
        while (end < lines.Length && !lines[end].TrimStart().StartsWith('['))
            end++;

        return (header, end);
    }

    private static string? KeyOf(string line)
    {
        int separator = line.IndexOf('=');
        return separator > 0 && !line.TrimStart().StartsWith('#') ? line[..separator].Trim() : null;
    }

    private static string? GetMainGroupValue(string[] lines, string key)
    {
        if (MainGroupRange(lines) is not { } range)
            return null;

        for (int i = range.Header + 1; i < range.End; i++)
        {
            if (KeyOf(lines[i]) == key)
                return lines[i][(lines[i].IndexOf('=') + 1)..].Trim();
        }

        return null;
    }

    private static string[] SetMainGroupValues(string[] lines, Dictionary<string, string> values)
    {
        if (MainGroupRange(lines) is not { } range)
            return lines;

        List<string> result = [.. lines];
        HashSet<string> pending = [.. values.Keys];
        for (int i = range.Header + 1; i < range.End; i++)
        {
            if (KeyOf(result[i]) is { } key && pending.Remove(key))
                result[i] = $"{key}={values[key]}";
        }

        result.InsertRange(range.Header + 1, values.Keys.Where(pending.Contains).Select(key => $"{key}={values[key]}"));
        return [.. result];
    }

    private static void WriteAtomically(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, content);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    [GeneratedRegex("@(COMMENT|EXEC|ICON)@")]
    private static partial Regex TemplatePlaceholder();

    [GeneratedRegex(@"^[a-z]{2,3}(_[A-Z]{2})?(@[A-Za-z]+)?$")]
    private static partial Regex LocaleKey();
}
