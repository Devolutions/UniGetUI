using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;

namespace UniGetUI.Interface;

internal static class IpcJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        TypeInfoResolver = IpcJsonContext.Default,
    };

    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, GetTypeInfo<T>());
    }

    public static T? Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize(json, GetTypeInfo<T>());
    }

    public static HttpContent CreateContent<T>(T value)
    {
        return new StringContent(
            Serialize(value),
            Encoding.UTF8,
            "application/json"
        );
    }

    public static Task WriteAsync<T>(HttpResponse response, T value)
    {
        response.ContentType = "application/json; charset=utf-8";
        return response.WriteAsync(Serialize(value));
    }

    private static JsonTypeInfo<T> GetTypeInfo<T>()
    {
        return (JsonTypeInfo<T>?)IpcJsonContext.Default.GetTypeInfo(typeof(T))
            ?? throw new InvalidOperationException(
                $"IPC JSON metadata for {typeof(T).FullName} was not generated."
            );
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true
)]
[JsonSerializable(typeof(IpcAppInfo))]
[JsonSerializable(typeof(IpcAppNavigateRequest))]
[JsonSerializable(typeof(IpcBackupStatus))]
[JsonSerializable(typeof(IpcGitHubAuthInfo))]
[JsonSerializable(typeof(IpcBackupCommandResult))]
[JsonSerializable(typeof(IpcLocalBackupResult))]
[JsonSerializable(typeof(IpcCloudBackupEntry))]
[JsonSerializable(typeof(IpcCloudBackupUploadResult))]
[JsonSerializable(typeof(IpcCloudBackupRequest))]
[JsonSerializable(typeof(IpcCloudBackupContentResult))]
[JsonSerializable(typeof(IpcCloudBackupRestoreResult))]
[JsonSerializable(typeof(IpcGitHubDeviceFlowRequest))]
[JsonSerializable(typeof(IpcGitHubAuthResult))]
[JsonSerializable(typeof(IpcBundleInfo))]
[JsonSerializable(typeof(IpcBundlePackageInfo))]
[JsonSerializable(typeof(IpcBundleImportRequest))]
[JsonSerializable(typeof(IpcBundleExportRequest))]
[JsonSerializable(typeof(IpcBundlePackageRequest))]
[JsonSerializable(typeof(IpcBundleInstallRequest))]
[JsonSerializable(typeof(IpcBundleSecurityEntry))]
[JsonSerializable(typeof(IpcBundleCommandResult))]
[JsonSerializable(typeof(IpcBundleImportResult))]
[JsonSerializable(typeof(IpcBundleExportResult))]
[JsonSerializable(typeof(IpcBundlePackageOperationResult))]
[JsonSerializable(typeof(IpcBundleInstallResult))]
[JsonSerializable(typeof(IpcCommandResult))]
[JsonSerializable(typeof(IpcDesktopShortcutInfo))]
[JsonSerializable(typeof(IpcDesktopShortcutRequest))]
[JsonSerializable(typeof(IpcDesktopShortcutOperationResult))]
[JsonSerializable(typeof(IpcAppLogEntry))]
[JsonSerializable(typeof(IpcOperationHistoryEntry))]
[JsonSerializable(typeof(IpcManagerLogTask))]
[JsonSerializable(typeof(IpcManagerLogInfo))]
[JsonSerializable(typeof(IpcManagerMaintenanceInfo))]
[JsonSerializable(typeof(IpcManagerMaintenanceRequest))]
[JsonSerializable(typeof(IpcManagerMaintenanceActionResult))]
[JsonSerializable(typeof(IpcManagerCapabilitiesInfo))]
[JsonSerializable(typeof(IpcManagerInfo))]
[JsonSerializable(typeof(IpcSourceInfo))]
[JsonSerializable(typeof(IpcSourceRequest))]
[JsonSerializable(typeof(IpcSourceOperationResult))]
[JsonSerializable(typeof(IpcSettingInfo))]
[JsonSerializable(typeof(IpcSettingValueRequest))]
[JsonSerializable(typeof(IpcManagerToggleRequest))]
[JsonSerializable(typeof(IpcOperationOutputLine))]
[JsonSerializable(typeof(IpcOperationInfo))]
[JsonSerializable(typeof(IpcOperationDetails))]
[JsonSerializable(typeof(IpcOperationOutputResult))]
[JsonSerializable(typeof(IpcPackageInfo))]
[JsonSerializable(typeof(IpcPackageActionRequest))]
[JsonSerializable(typeof(IpcPackageOperationResult))]
[JsonSerializable(typeof(IpcPackageDependencyInfo))]
[JsonSerializable(typeof(IpcPackageDetailsInfo))]
[JsonSerializable(typeof(IpcIgnoredUpdateInfo))]
[JsonSerializable(typeof(IpcSecureSettingInfo))]
[JsonSerializable(typeof(IpcSecureSettingRequest))]
[JsonSerializable(typeof(IpcStatus))]
[JsonSerializable(typeof(IpcEndpointRegistration))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(IReadOnlyList<IpcManagerInfo>))]
[JsonSerializable(typeof(IReadOnlyList<IpcSourceInfo>))]
[JsonSerializable(typeof(IReadOnlyList<IpcSettingInfo>))]
[JsonSerializable(typeof(IReadOnlyList<IpcSecureSettingInfo>))]
[JsonSerializable(typeof(IReadOnlyList<IpcDesktopShortcutInfo>))]
[JsonSerializable(typeof(IReadOnlyList<IpcAppLogEntry>))]
[JsonSerializable(typeof(IReadOnlyList<IpcOperationHistoryEntry>))]
[JsonSerializable(typeof(IReadOnlyList<IpcManagerLogInfo>))]
[JsonSerializable(typeof(IReadOnlyList<IpcOperationInfo>))]
[JsonSerializable(typeof(IReadOnlyList<IpcOperationOutputLine>))]
[JsonSerializable(typeof(IReadOnlyList<IpcPackageInfo>))]
[JsonSerializable(typeof(IReadOnlyList<IpcPackageDependencyInfo>))]
[JsonSerializable(typeof(IReadOnlyList<IpcIgnoredUpdateInfo>))]
[JsonSerializable(typeof(IReadOnlyList<IpcBundlePackageInfo>))]
[JsonSerializable(typeof(IReadOnlyList<IpcBundleSecurityEntry>))]
[JsonSerializable(typeof(IReadOnlyList<IpcPackageOperationResult>))]
[JsonSerializable(typeof(IReadOnlyList<IpcCloudBackupEntry>))]
[JsonSerializable(typeof(IpcManagerInfo[]))]
[JsonSerializable(typeof(IpcSourceInfo[]))]
[JsonSerializable(typeof(IpcSettingInfo[]))]
[JsonSerializable(typeof(IpcSecureSettingInfo[]))]
[JsonSerializable(typeof(IpcDesktopShortcutInfo[]))]
[JsonSerializable(typeof(IpcAppLogEntry[]))]
[JsonSerializable(typeof(IpcOperationHistoryEntry[]))]
[JsonSerializable(typeof(IpcManagerLogInfo[]))]
[JsonSerializable(typeof(IpcOperationInfo[]))]
[JsonSerializable(typeof(IpcOperationOutputLine[]))]
[JsonSerializable(typeof(IpcPackageInfo[]))]
[JsonSerializable(typeof(IpcPackageDependencyInfo[]))]
[JsonSerializable(typeof(IpcIgnoredUpdateInfo[]))]
[JsonSerializable(typeof(IpcBundlePackageInfo[]))]
[JsonSerializable(typeof(IpcBundleSecurityEntry[]))]
[JsonSerializable(typeof(IpcPackageOperationResult[]))]
[JsonSerializable(typeof(IpcCloudBackupEntry[]))]
internal sealed partial class IpcJsonContext : JsonSerializerContext;
