using UniGetUI.Core.SettingsEngine.SecureSettings;

namespace UniGetUI.Interface;

public sealed class IpcSecureSettingInfo
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string UserName { get; set; } = "";
    public bool IsCurrentUser { get; set; }
    public bool Enabled { get; set; }
}

public sealed class IpcSecureSettingRequest
{
    public string SettingKey { get; set; } = "";
    public string? UserName { get; set; }
    public bool Enabled { get; set; }
}

public static class IpcSecureSettingsApi
{
    public static IReadOnlyList<IpcSecureSettingInfo> ListSettings(string? userName = null)
    {
        string resolvedUser = ResolveUserName(userName);
        return Enum.GetValues<SecureSettings.K>()
            .Where(key => key != SecureSettings.K.Unset)
            .OrderBy(key => key.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(key => ToSecureSettingInfo(key, resolvedUser))
            .ToArray();
    }

    public static IpcSecureSettingInfo GetSetting(string settingKey, string? userName = null)
    {
        return ToSecureSettingInfo(ResolveSettingKey(settingKey), ResolveUserName(userName));
    }

    public static async Task<IpcSecureSettingInfo> SetSettingAsync(
        IpcSecureSettingRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = ResolveSettingKey(request.SettingKey);
        string userName = ResolveUserName(request.UserName);

        bool success = userName.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase)
            ? await SecureSettings.TrySet(key, request.Enabled)
            : SecureSettings.ApplyForUser(userName, SecureSettings.ResolveKey(key), request.Enabled) == 0;

        if (!success)
        {
            throw new InvalidOperationException(
                $"Could not update secure setting \"{SecureSettings.ResolveKey(key)}\" for user \"{userName}\"."
            );
        }

        return ToSecureSettingInfo(key, userName);
    }

    private static IpcSecureSettingInfo ToSecureSettingInfo(
        SecureSettings.K key,
        string userName
    )
    {
        return new IpcSecureSettingInfo
        {
            Key = key.ToString(),
            Name = SecureSettings.ResolveKey(key),
            UserName = userName,
            IsCurrentUser = userName.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase),
            Enabled = SecureSettings.GetForUser(userName, key),
        };
    }

    private static SecureSettings.K ResolveSettingKey(string settingKey)
    {
        if (string.IsNullOrWhiteSpace(settingKey))
        {
            throw new InvalidOperationException("The secure setting key parameter is required.");
        }

        if (Enum.TryParse(settingKey, true, out SecureSettings.K enumKey) && enumKey != SecureSettings.K.Unset)
        {
            return enumKey;
        }

        foreach (var key in Enum.GetValues<SecureSettings.K>())
        {
            if (
                key != SecureSettings.K.Unset
                && SecureSettings.ResolveKey(key).Equals(settingKey, StringComparison.OrdinalIgnoreCase)
            )
            {
                return key;
            }
        }

        throw new InvalidOperationException($"No secure setting matching \"{settingKey}\" was found.");
    }

    private static string ResolveUserName(string? userName)
    {
        return string.IsNullOrWhiteSpace(userName) ? Environment.UserName : userName.Trim();
    }
}
