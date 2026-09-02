using System.Collections.Concurrent;
using System.Diagnostics;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;

namespace UniGetUI.Core.SettingsEngine.SecureSettings;

public static class SecureSettings
{
    public static string? TEST_SecureSettingsRootOverride { private get; set; }

    // Various predefined secure settings keys
    public enum K
    {
        AllowCLIArguments,
        AllowImportingCLIArguments,
        AllowPrePostOpCommand,
        AllowImportPrePostOpCommands,
        ForceUserGSudo,
        AllowCustomManagerPaths,
        Unset,
    };

    public static string ResolveKey(K key)
    {
        return key switch
        {
            K.AllowCLIArguments => "AllowCLIArguments",
            K.AllowImportingCLIArguments => "AllowImportingCLIArguments",
            K.AllowPrePostOpCommand => "AllowPrePostInstallCommands",
            K.AllowImportPrePostOpCommands => "AllowImportingPrePostInstallCommands",
            K.ForceUserGSudo => "ForceUserGSudo",
            K.AllowCustomManagerPaths => "AllowCustomManagerPaths",

            K.Unset => throw new InvalidDataException("SecureSettings key was unset!"),
            _ => throw new KeyNotFoundException(
                $"The SecureSettings key {key} was not found on the ResolveKey map"
            ),
        };
    }

    private static readonly ConcurrentDictionary<string, bool> _cache = new();

    public static class Args
    {
        public const string ENABLE_FOR_USER = "--enable-secure-setting-for-user";
        public const string DISABLE_FOR_USER = "--disable-secure-setting-for-user";
    }

    public static bool Get(K key)
    {
        return GetForUser(Environment.UserName, key);
    }

    public static bool GetForUser(string username, K key)
    {
        return GetForUser(username, ResolveKey(key));
    }

    public static bool GetForUser(string username, string setting)
    {
        if (
            !TryResolveSecureSettingPath(
                username,
                setting,
                out string settingsLocation,
                out string settingFile
            )
        )
        {
            return false;
        }

        string purifiedSetting = CoreTools.MakeValidFileName(setting);
        string purifiedUser = CoreTools.MakeValidFileName(username);
        string cacheKey = $"{purifiedUser}|{purifiedSetting}";
        if (_cache.TryGetValue(cacheKey, out var value))
        {
            return value;
        }

        if (!Directory.Exists(settingsLocation))
        {
            _cache[cacheKey] = false;
            return false;
        }

        bool exists = File.Exists(settingFile);
        _cache[cacheKey] = exists;
        return exists;
    }

    public static async Task<bool> TrySet(K key, bool enabled)
    {
        string purifiedSetting = CoreTools.MakeValidFileName(ResolveKey(key));
        string purifiedUser = CoreTools.MakeValidFileName(Environment.UserName);
        _cache.TryRemove($"{purifiedUser}|{purifiedSetting}", out _);

        if (!OperatingSystem.IsWindows())
        {
            return ApplyForUser(purifiedUser, purifiedSetting, enabled) is 0;
        }

        using Process p = new Process();
        p.StartInfo = new()
        {
            UseShellExecute = true,
            CreateNoWindow = true,
            FileName = CoreData.UniGetUIExecutableFile,
            Verb = "runas",
            ArgumentList =
            {
                enabled ? Args.ENABLE_FOR_USER : Args.DISABLE_FOR_USER,
                purifiedUser,
                purifiedSetting,
            },
        };

        p.Start();
        await p.WaitForExitAsync();
        return p.ExitCode is 0;
    }

    public static int ApplyForUser(string username, string setting, bool enable)
    {
        try
        {
            string purifiedSetting = CoreTools.MakeValidFileName(setting);
            string purifiedUser = CoreTools.MakeValidFileName(username);
            _cache.TryRemove($"{purifiedUser}|{purifiedSetting}", out _);

            if (
                !TryResolveSecureSettingPath(
                    username,
                    setting,
                    out string settingsLocation,
                    out string settingFile
                )
            )
            {
                Console.WriteLine(
                    $"Refused a secure setting path outside the secure settings root: "
                        + $"user='{username}', setting='{setting}'"
                );
                return -1;
            }

            if (!Directory.Exists(settingsLocation))
            {
                Directory.CreateDirectory(settingsLocation);
            }

            if (enable)
            {
                File.WriteAllText(settingFile, "");
            }
            else
            {
                if (File.Exists(settingFile))
                {
                    File.Delete(settingFile);
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            return -1;
        }
    }

    private static bool TryResolveSecureSettingPath(
        string username,
        string setting,
        out string settingsLocation,
        out string settingFile
    )
    {
        settingsLocation = string.Empty;
        settingFile = string.Empty;

        if (!IsSafeRawComponent(username) || !IsSafeRawComponent(setting))
            return false;

        string purifiedUser = CoreTools.MakeValidFileName(username);
        string purifiedSetting = CoreTools.MakeValidFileName(setting);

        if (!IsSafePathComponent(purifiedUser) || !IsSafePathComponent(purifiedSetting))
            return false;

        string root = Path.GetFullPath(GetSecureSettingsRoot());
        string location = Path.GetFullPath(Path.Join(root, purifiedUser));
        string file = Path.GetFullPath(Path.Join(location, purifiedSetting));

        if (!IsDirectChildOf(location, root) || !IsDirectChildOf(file, location))
            return false;

        if (!purifiedUser.Equals(Path.GetFileName(location), StringComparison.Ordinal))
            return false;

        if (!purifiedSetting.Equals(Path.GetFileName(file), StringComparison.Ordinal))
            return false;

        if (
            IsLink(new DirectoryInfo(root))
            || IsLink(new DirectoryInfo(location))
            || IsLink(new FileInfo(file))
        )
            return false;

        settingsLocation = location;
        settingFile = file;
        return true;
    }

    private static bool IsSafeRawComponent(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !value.All(character => character is '.' || char.IsWhiteSpace(character));
    }

    private static bool IsSafePathComponent(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value is not ("." or "..")
            && value.Equals(Path.GetFileName(value), StringComparison.Ordinal);
    }

    private static bool IsLink(FileSystemInfo entry)
    {
        try
        {
            return entry.LinkTarget is not null;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsDirectChildOf(string candidate, string parent)
    {
        return string.Equals(
            Path.GetDirectoryName(candidate),
            parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal
        );
    }

    private static string GetSecureSettingsRoot()
    {
        if (TEST_SecureSettingsRootOverride is not null)
        {
            return TEST_SecureSettingsRootOverride;
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Join(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "UniGetUI",
                "SecureSettings"
            );
        }

        return Path.Join(CoreData.UniGetUIDataDirectory, "SecureSettings");
    }

}
