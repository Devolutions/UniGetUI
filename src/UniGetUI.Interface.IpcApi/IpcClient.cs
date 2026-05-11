using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;

namespace UniGetUI.Interface;

public sealed class IpcClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _token;

    public IpcTransportOptions TransportOptions { get; }

    private IpcClient(IpcTransportOptions transportOptions, string? token = null)
    {
        TransportOptions = transportOptions;
        _token = token ?? string.Empty;
        _httpClient = CreateHttpClient(transportOptions);
    }

    public static IpcClient CreateForCli(IReadOnlyList<string>? args = null)
    {
        args ??= Environment.GetCommandLineArgs();
        IpcTransportOptions requestedOptions = IpcTransportOptions.LoadForClient(args);

        if (IpcTransportOptions.HasExplicitClientOverride(args))
        {
            return new IpcClient(
                requestedOptions,
                WaitForExplicitSessionToken(requestedOptions)
            );
        }

        var preferredRegistration = SelectLiveRegistration(
            IpcTransportOptions.OrderRegistrationsForCliSelection(
                IpcTransportOptions.LoadPersistedRegistrations()
            )
        );

        return preferredRegistration is not null
            ? new IpcClient(
                preferredRegistration.ToTransportOptions(),
                preferredRegistration.Token
            )
            : new IpcClient(requestedOptions);
    }

    private static string? WaitForExplicitSessionToken(IpcTransportOptions requestedOptions)
    {
        Stopwatch timeout = Stopwatch.StartNew();

        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            var matchingRegistrations = IpcTransportOptions.LoadPersistedRegistrations()
                .Where(candidate => candidate.Matches(requestedOptions))
                .ToArray();
            var registration = SelectLiveRegistration(matchingRegistrations);
            string? token = registration?.Token
                ?? matchingRegistrations.FirstOrDefault(candidate =>
                    !string.IsNullOrWhiteSpace(candidate.Token)
                )?.Token;

            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }

            Thread.Sleep(100);
        }

        return null;
    }

    public async Task<IpcStatus> GetStatusAsync()
    {
        try
        {
            string json = await SendAsync(HttpMethod.Get, IpcHttpRoutes.Path("/status"));
            var status = IpcJson.Deserialize<IpcStatus>(json);
            if (status is not null)
            {
                return status;
            }
        }
        catch (Exception ex) when (IsConnectivityException(ex))
        {
            Logger.Debug($"IPC API status probe failed: {ex.Message}");
        }

        return new IpcStatus
        {
            Running = false,
            Transport = TransportOptions.TransportKind switch
            {
                IpcTransportKind.NamedPipe => "named-pipe",
                _ => "tcp",
            },
            TcpPort = TransportOptions.TcpPort,
            NamedPipeName = TransportOptions.NamedPipeName,
            NamedPipePath = TransportOptions.NamedPipePath ?? "",
            BaseAddress = TransportOptions.BaseAddressString,
        };
    }

    public async Task<IpcAppInfo> GetAppInfoAsync()
    {
        return await ReadAuthenticatedJsonAsync<IpcAppInfo>(
            HttpMethod.Get,
            IpcHttpRoutes.Path("/app")
        )
            ?? new IpcAppInfo();
    }

    public async Task<IReadOnlyList<IpcOperationInfo>> ListOperationsAsync()
    {
        return await ReadAuthenticatedJsonAsync<IReadOnlyList<IpcOperationInfo>>(
                HttpMethod.Get,
                IpcHttpRoutes.Path("/operations")
            )
            ?? [];
    }

    public async Task<IpcOperationDetails?> GetOperationAsync(string operationId)
    {
        return await ReadAuthenticatedJsonAsync<IpcOperationDetails>(
            HttpMethod.Get,
            IpcHttpRoutes.Path($"/operations/{Uri.EscapeDataString(operationId)}")
        );
    }

    public async Task<IpcOperationOutputResult> GetOperationOutputAsync(
        string operationId,
        int? tailLines = null
    )
    {
        Dictionary<string, string>? parameters = null;
        if (tailLines.HasValue)
        {
            parameters = new Dictionary<string, string>
            {
                ["tailLines"] = tailLines.Value.ToString(),
            };
        }

        return await ReadAuthenticatedJsonAsync<IpcOperationOutputResult>(
                HttpMethod.Get,
                IpcHttpRoutes.Path($"/operations/{Uri.EscapeDataString(operationId)}/output"),
                parameters
            )
            ?? new IpcOperationOutputResult
            {
                OperationId = operationId,
            };
    }

    public async Task<IpcOperationDetails> WaitForOperationAsync(
        string operationId,
        int timeoutSeconds = 300,
        int delayMilliseconds = 1000
    )
    {
        timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 3600);
        delayMilliseconds = Math.Clamp(delayMilliseconds, 100, 10000);

        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            var operation = await GetOperationAsync(operationId)
                ?? throw new InvalidOperationException(
                    $"No tracked operation with id \"{operationId}\" was found."
                );

            if (
                operation.Status is "succeeded" or "failed" or "canceled"
            )
            {
                return operation;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    $"Timed out while waiting for operation {operationId}."
                );
            }

            await Task.Delay(delayMilliseconds);
        }
    }

    public async Task<IpcCommandResult> CancelOperationAsync(string operationId)
    {
        return await ReadAuthenticatedJsonAsync<IpcCommandResult>(
                HttpMethod.Post,
                IpcHttpRoutes.Path($"/operations/{Uri.EscapeDataString(operationId)}/cancel")
            )
            ?? new IpcCommandResult
            {
                Status = "error",
                Command = "cancel-operation",
                Message = "The IPC API returned an empty response.",
            };
    }

    public async Task<IpcCommandResult> RetryOperationAsync(
        string operationId,
        string? mode = null
    )
    {
        Dictionary<string, string>? parameters = null;
        if (!string.IsNullOrWhiteSpace(mode))
        {
            parameters = new Dictionary<string, string>
            {
                ["mode"] = mode,
            };
        }

        return await ReadAuthenticatedJsonAsync<IpcCommandResult>(
                HttpMethod.Post,
                IpcHttpRoutes.Path($"/operations/{Uri.EscapeDataString(operationId)}/retry"),
                parameters
            )
            ?? new IpcCommandResult
            {
                Status = "error",
                Command = "retry-operation",
                Message = "The IPC API returned an empty response.",
            };
    }

    public async Task<IpcCommandResult> ReorderOperationAsync(
        string operationId,
        string action
    )
    {
        return await ReadAuthenticatedJsonAsync<IpcCommandResult>(
                HttpMethod.Post,
                IpcHttpRoutes.Path($"/operations/{Uri.EscapeDataString(operationId)}/reorder"),
                new Dictionary<string, string> { ["action"] = action }
            )
            ?? new IpcCommandResult
            {
                Status = "error",
                Command = "reorder-operation",
                Message = "The IPC API returned an empty response.",
            };
    }

    public async Task<IpcCommandResult> ForgetOperationAsync(string operationId)
    {
        return await ReadAuthenticatedJsonAsync<IpcCommandResult>(
                HttpMethod.Post,
                IpcHttpRoutes.Path($"/operations/{Uri.EscapeDataString(operationId)}/forget")
            )
            ?? new IpcCommandResult
            {
                Status = "error",
                Command = "forget-operation",
                Message = "The IPC API returned an empty response.",
            };
    }

    public async Task<IpcCommandResult> ShowAppAsync()
    {
        return await ReadAuthenticatedJsonAsync<IpcCommandResult>(
                HttpMethod.Post,
                IpcHttpRoutes.Path("/app/show")
            )
            ?? new IpcCommandResult
            {
                Status = "error",
                Command = "show-app",
                Message = "The IPC API returned an empty response.",
            };
    }

    public async Task<IpcCommandResult> NavigateAppAsync(
        IpcAppNavigateRequest request
    )
    {
        return await ReadAuthenticatedJsonAsync<IpcCommandResult>(
                HttpMethod.Post,
                IpcHttpRoutes.Path("/app/navigate"),
                BuildAppNavigateParameters(request)
            )
            ?? new IpcCommandResult
            {
                Status = "error",
                Command = "navigate-app",
                Message = "The IPC API returned an empty response.",
            };
    }

    public async Task<IpcCommandResult> QuitAppAsync()
    {
        return await ReadAuthenticatedJsonAsync<IpcCommandResult>(
                HttpMethod.Post,
                IpcHttpRoutes.Path("/app/quit")
            )
            ?? new IpcCommandResult
            {
                Status = "error",
                Command = "quit-app",
                Message = "The IPC API returned an empty response.",
            };
    }

    public async Task<IReadOnlyList<IpcManagerInfo>> ListManagersAsync()
    {
        return await ReadAuthenticatedJsonAsync<IReadOnlyList<IpcManagerInfo>>(
            HttpMethod.Get,
            IpcHttpRoutes.Path("/managers")
        ) ?? [];
    }

    public async Task<IpcManagerMaintenanceInfo?> GetManagerMaintenanceAsync(
        string managerName
    )
    {
        return await ReadAuthenticatedJsonAsync<IpcManagerMaintenanceInfo>(
            HttpMethod.Get,
            IpcHttpRoutes.Path("/managers/maintenance"),
            new Dictionary<string, string> { ["manager"] = managerName }
        );
    }

    public async Task<IpcManagerMaintenanceActionResult> ReloadManagerAsync(
        IpcManagerMaintenanceRequest request
    )
    {
        return await SendManagerMaintenanceActionAsync(
            IpcHttpRoutes.Path("/managers/maintenance/reload"),
            request
        );
    }

    public async Task<IpcManagerMaintenanceActionResult> SetManagerExecutablePathAsync(
        IpcManagerMaintenanceRequest request
    )
    {
        return await SendManagerMaintenanceActionAsync(
            IpcHttpRoutes.Path("/managers/maintenance/executable/set"),
            request
        );
    }

    public async Task<IpcManagerMaintenanceActionResult> ClearManagerExecutablePathAsync(
        IpcManagerMaintenanceRequest request
    )
    {
        return await SendManagerMaintenanceActionAsync(
            IpcHttpRoutes.Path("/managers/maintenance/executable/clear"),
            request
        );
    }

    public async Task<IpcManagerMaintenanceActionResult> RunManagerActionAsync(
        IpcManagerMaintenanceRequest request
    )
    {
        return await SendManagerMaintenanceActionAsync(
            IpcHttpRoutes.Path("/managers/maintenance/action"),
            request
        );
    }

    public async Task<IReadOnlyList<IpcSourceInfo>> ListSourcesAsync(string? managerName = null)
    {
        Dictionary<string, string>? parameters = null;
        if (!string.IsNullOrWhiteSpace(managerName))
        {
            parameters = new Dictionary<string, string> { ["manager"] = managerName };
        }

        return await ReadAuthenticatedJsonAsync<IReadOnlyList<IpcSourceInfo>>(
            HttpMethod.Get,
            IpcHttpRoutes.Path("/sources"),
            parameters
        ) ?? [];
    }

    public async Task<IpcSourceOperationResult> AddSourceAsync(IpcSourceRequest request)
    {
        return await SendSourceOperationAsync(IpcHttpRoutes.Path("/sources/add"), request);
    }

    public async Task<IpcSourceOperationResult> RemoveSourceAsync(IpcSourceRequest request)
    {
        return await SendSourceOperationAsync(IpcHttpRoutes.Path("/sources/remove"), request);
    }

    public async Task<IReadOnlyList<IpcSettingInfo>> ListSettingsAsync()
    {
        return await ReadAuthenticatedJsonAsync<IReadOnlyList<IpcSettingInfo>>(
            HttpMethod.Get,
            IpcHttpRoutes.Path("/settings")
        ) ?? [];
    }

    public async Task<IReadOnlyList<IpcSecureSettingInfo>> ListSecureSettingsAsync(
        string? userName = null
    )
    {
        Dictionary<string, string>? parameters = null;
        if (!string.IsNullOrWhiteSpace(userName))
        {
            parameters = new Dictionary<string, string> { ["user"] = userName };
        }

        return await ReadAuthenticatedJsonAsync<IReadOnlyList<IpcSecureSettingInfo>>(
            HttpMethod.Get,
            IpcHttpRoutes.Path("/secure-settings"),
            parameters
        ) ?? [];
    }

    public async Task<IpcSecureSettingInfo?> GetSecureSettingAsync(
        string key,
        string? userName = null
    )
    {
        Dictionary<string, string> parameters = new() { ["key"] = key };
        if (!string.IsNullOrWhiteSpace(userName))
        {
            parameters["user"] = userName;
        }

        return await ReadAuthenticatedJsonAsync<IpcSecureSettingInfo>(
            HttpMethod.Get,
            IpcHttpRoutes.Path("/secure-settings/item"),
            parameters
        );
    }

    public async Task<IpcSecureSettingInfo?> SetSecureSettingAsync(
        IpcSecureSettingRequest request
    )
    {
        Dictionary<string, string> parameters = new()
        {
            ["key"] = request.SettingKey,
            ["enabled"] = request.Enabled ? "true" : "false",
        };
        if (!string.IsNullOrWhiteSpace(request.UserName))
        {
            parameters["user"] = request.UserName;
        }

        return await ReadAuthenticatedJsonAsync<IpcSecureSettingInfo>(
            HttpMethod.Post,
            IpcHttpRoutes.Path("/secure-settings/set"),
            parameters
        );
    }

    public async Task<IpcSettingInfo?> GetSettingAsync(string key)
    {
        return await ReadAuthenticatedJsonAsync<IpcSettingInfo>(
            HttpMethod.Get,
            IpcHttpRoutes.Path("/settings/item"),
            new Dictionary<string, string> { ["key"] = key }
        );
    }

    public async Task<IpcSettingInfo?> SetSettingAsync(IpcSettingValueRequest request)
    {
        Dictionary<string, string> parameters = new() { ["key"] = request.SettingKey };
        if (request.Enabled.HasValue)
        {
            parameters["enabled"] = request.Enabled.Value ? "true" : "false";
        }

        if (request.Value is not null)
        {
            parameters["value"] = request.Value;
        }

        return await ReadAuthenticatedJsonAsync<IpcSettingInfo>(
            HttpMethod.Post,
            IpcHttpRoutes.Path("/settings/set"),
            parameters
        );
    }

    public async Task<IpcSettingInfo?> ClearSettingAsync(string key)
    {
        return await ReadAuthenticatedJsonAsync<IpcSettingInfo>(
            HttpMethod.Post,
            IpcHttpRoutes.Path("/settings/clear"),
            new Dictionary<string, string> { ["key"] = key }
        );
    }

    public async Task<IpcManagerInfo?> SetManagerEnabledAsync(
        IpcManagerToggleRequest request
    )
    {
        return await ReadAuthenticatedJsonAsync<IpcManagerInfo>(
            HttpMethod.Post,
            IpcHttpRoutes.Path("/managers/set-enabled"),
            new Dictionary<string, string>
            {
                ["manager"] = request.ManagerName,
                ["enabled"] = request.Enabled ? "true" : "false",
            }
        );
    }

    public async Task<IpcManagerInfo?> SetManagerUpdateNotificationsAsync(
        IpcManagerToggleRequest request
    )
    {
        return await ReadAuthenticatedJsonAsync<IpcManagerInfo>(
            HttpMethod.Post,
            IpcHttpRoutes.Path("/managers/set-update-notifications"),
            new Dictionary<string, string>
            {
                ["manager"] = request.ManagerName,
                ["enabled"] = request.Enabled ? "true" : "false",
            }
        );
    }

    public async Task<IpcCommandResult> ResetSettingsAsync()
    {
        return await ReadAuthenticatedJsonAsync<IpcCommandResult>(
                HttpMethod.Post,
                IpcHttpRoutes.Path("/settings/reset")
            )
            ?? new IpcCommandResult
            {
                Status = "error",
                Message = "The IPC API returned an empty response.",
            };
    }

    public async Task<IReadOnlyList<IpcDesktopShortcutInfo>> ListDesktopShortcutsAsync()
    {
        return await ReadAuthenticatedJsonAsync<IReadOnlyList<IpcDesktopShortcutInfo>>(
            HttpMethod.Get,
            IpcHttpRoutes.Path("/desktop-shortcuts")
        ) ?? [];
    }

    public async Task<IpcDesktopShortcutOperationResult> SetDesktopShortcutAsync(
        IpcDesktopShortcutRequest request
    )
    {
        return await SendDesktopShortcutOperationAsync(
            IpcHttpRoutes.Path("/desktop-shortcuts/set"),
            request
        );
    }

    public async Task<IpcDesktopShortcutOperationResult> ResetDesktopShortcutAsync(
        string shortcutPath
    )
    {
        return await SendDesktopShortcutOperationAsync(
            IpcHttpRoutes.Path("/desktop-shortcuts/reset"),
            new IpcDesktopShortcutRequest { Path = shortcutPath }
        );
    }

    public async Task<IpcCommandResult> ResetDesktopShortcutsAsync()
    {
        return await ReadAuthenticatedJsonAsync<IpcCommandResult>(
                HttpMethod.Post,
                IpcHttpRoutes.Path("/desktop-shortcuts/reset-all")
            )
            ?? new IpcCommandResult
            {
                Status = "error",
                Message = "The IPC API returned an empty response.",
            };
    }

    public async Task<IReadOnlyList<IpcAppLogEntry>> GetAppLogAsync(int level = 4)
    {
        return await ReadAuthenticatedJsonAsync<IReadOnlyList<IpcAppLogEntry>>(
            HttpMethod.Get,
            IpcHttpRoutes.Path("/logs/app"),
            new Dictionary<string, string> { ["level"] = level.ToString() }
        ) ?? [];
    }

    public async Task<IReadOnlyList<IpcOperationHistoryEntry>> GetOperationHistoryAsync()
    {
        return await ReadAuthenticatedJsonAsync<IReadOnlyList<IpcOperationHistoryEntry>>(
            HttpMethod.Get,
            IpcHttpRoutes.Path("/logs/history")
        ) ?? [];
    }

    public async Task<IReadOnlyList<IpcManagerLogInfo>> GetManagerLogAsync(
        string? managerName = null,
        bool verbose = false
    )
    {
        Dictionary<string, string>? parameters = null;
        if (!string.IsNullOrWhiteSpace(managerName) || verbose)
        {
            parameters = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(managerName))
            {
                parameters["manager"] = managerName;
            }

            if (verbose)
            {
                parameters["verbose"] = "true";
            }
        }

        return await ReadAuthenticatedJsonAsync<IReadOnlyList<IpcManagerLogInfo>>(
            HttpMethod.Get,
            IpcHttpRoutes.Path("/logs/manager"),
            parameters
        ) ?? [];
    }

    public async Task<IpcBackupStatus?> GetBackupStatusAsync()
    {
        return await ReadAuthenticatedJsonAsync<IpcBackupStatus>(
            HttpMethod.Get,
            IpcHttpRoutes.Path("/backups/status")
        );
    }

    public async Task<IpcLocalBackupResult> CreateLocalBackupAsync()
    {
        return await ReadAuthenticatedJsonAsync<IpcLocalBackupResult>(
                HttpMethod.Post,
                IpcHttpRoutes.Path("/backups/local/create")
            )
            ?? new IpcLocalBackupResult
            {
                Status = "error",
                Message = "The IPC API returned an empty response.",
            };
    }

    public async Task<IpcGitHubAuthResult> StartGitHubDeviceFlowAsync(
        IpcGitHubDeviceFlowRequest request
    )
    {
        return await ReadAuthenticatedJsonWithBodyAsync<
                IpcGitHubAuthResult,
                IpcGitHubDeviceFlowRequest
            >(IpcHttpRoutes.Path("/backups/github/sign-in/start"), request)
            ?? new IpcGitHubAuthResult
            {
                Status = "error",
                Message = "The IPC API returned an empty response.",
            };
    }

    public async Task<IpcGitHubAuthResult> CompleteGitHubDeviceFlowAsync()
    {
        return await ReadAuthenticatedJsonAsync<IpcGitHubAuthResult>(
                HttpMethod.Post,
                IpcHttpRoutes.Path("/backups/github/sign-in/complete")
            )
            ?? new IpcGitHubAuthResult
            {
                Status = "error",
                Message = "The IPC API returned an empty response.",
            };
    }

    public async Task<IpcGitHubAuthResult> SignOutGitHubAsync()
    {
        return await ReadAuthenticatedJsonAsync<IpcGitHubAuthResult>(
                HttpMethod.Post,
                IpcHttpRoutes.Path("/backups/github/sign-out")
            )
            ?? new IpcGitHubAuthResult
            {
                Status = "error",
                Message = "The IPC API returned an empty response.",
            };
    }

    public async Task<IReadOnlyList<IpcCloudBackupEntry>> ListCloudBackupsAsync()
    {
        return await ReadAuthenticatedJsonAsync<IReadOnlyList<IpcCloudBackupEntry>>(
            HttpMethod.Get,
            IpcHttpRoutes.Path("/backups/cloud")
        ) ?? [];
    }

    public async Task<IpcCloudBackupUploadResult> CreateCloudBackupAsync()
    {
        return await ReadAuthenticatedJsonAsync<IpcCloudBackupUploadResult>(
                HttpMethod.Post,
                IpcHttpRoutes.Path("/backups/cloud/create")
            )
            ?? new IpcCloudBackupUploadResult
            {
                Status = "error",
                Message = "The IPC API returned an empty response.",
            };
    }

    public async Task<IpcCloudBackupContentResult> DownloadCloudBackupAsync(
        IpcCloudBackupRequest request
    )
    {
        return await ReadAuthenticatedJsonWithBodyAsync<
                IpcCloudBackupContentResult,
                IpcCloudBackupRequest
            >(IpcHttpRoutes.Path("/backups/cloud/download"), request)
            ?? new IpcCloudBackupContentResult
            {
                Status = "error",
                Message = "The IPC API returned an empty response.",
            };
    }

    public async Task<IpcCloudBackupRestoreResult> RestoreCloudBackupAsync(
        IpcCloudBackupRequest request
    )
    {
        return await ReadAuthenticatedJsonWithBodyAsync<
                IpcCloudBackupRestoreResult,
                IpcCloudBackupRequest
            >(IpcHttpRoutes.Path("/backups/cloud/restore"), request)
            ?? new IpcCloudBackupRestoreResult
            {
                Status = "error",
                Message = "The IPC API returned an empty response.",
            };
    }

    public async Task<IpcBundleInfo> GetBundleAsync()
    {
        return await ReadAuthenticatedJsonAsync<IpcBundleInfo>(
            HttpMethod.Get,
            IpcHttpRoutes.Path("/bundles")
        )
            ?? new IpcBundleInfo();
    }

    public async Task<IpcCommandResult> ResetBundleAsync()
    {
        return await ReadAuthenticatedJsonAsync<IpcCommandResult>(
                HttpMethod.Post,
                IpcHttpRoutes.Path("/bundles/reset")
            )
            ?? new IpcCommandResult
            {
                Status = "error",
                Message = "The IPC API returned an empty response.",
            };
    }

    public async Task<IpcBundleImportResult> ImportBundleAsync(
        IpcBundleImportRequest request
    )
    {
        return await ReadAuthenticatedJsonWithBodyAsync<
                IpcBundleImportResult,
                IpcBundleImportRequest
            >(IpcHttpRoutes.Path("/bundles/import"), request)
            ?? new IpcBundleImportResult
            {
                Status = "error",
                Message = "The IPC API returned an empty response.",
            };
    }

    public async Task<IpcBundleExportResult> ExportBundleAsync(
        IpcBundleExportRequest request
    )
    {
        return await ReadAuthenticatedJsonWithBodyAsync<
                IpcBundleExportResult,
                IpcBundleExportRequest
            >(IpcHttpRoutes.Path("/bundles/export"), request)
            ?? new IpcBundleExportResult
            {
                Status = "error",
                Message = "The IPC API returned an empty response.",
            };
    }

    public async Task<IpcBundlePackageOperationResult> AddBundlePackageAsync(
        IpcBundlePackageRequest request
    )
    {
        return await ReadAuthenticatedJsonWithBodyAsync<
                IpcBundlePackageOperationResult,
                IpcBundlePackageRequest
            >(IpcHttpRoutes.Path("/bundles/add"), request)
            ?? new IpcBundlePackageOperationResult
            {
                Status = "error",
                Message = "The IPC API returned an empty response.",
            };
    }

    public async Task<IpcBundlePackageOperationResult> RemoveBundlePackageAsync(
        IpcBundlePackageRequest request
    )
    {
        return await ReadAuthenticatedJsonWithBodyAsync<
                IpcBundlePackageOperationResult,
                IpcBundlePackageRequest
            >(IpcHttpRoutes.Path("/bundles/remove"), request)
            ?? new IpcBundlePackageOperationResult
            {
                Status = "error",
                Message = "The IPC API returned an empty response.",
            };
    }

    public async Task<IpcBundleInstallResult> InstallBundleAsync(
        IpcBundleInstallRequest request
    )
    {
        return await ReadAuthenticatedJsonWithBodyAsync<
                IpcBundleInstallResult,
                IpcBundleInstallRequest
            >(IpcHttpRoutes.Path("/bundles/install"), request)
            ?? new IpcBundleInstallResult
            {
                Status = "error",
                Message = "The IPC API returned an empty response.",
            };
    }

    public async Task<IpcCommandResult> OpenWindowAsync()
    {
        return await ShowAppAsync();
    }

    public async Task<IpcCommandResult> OpenUpdatesAsync()
    {
        return await NavigateAppAsync(
            new IpcAppNavigateRequest
            {
                Page = "updates",
            }
        );
    }

    public async Task<IpcCommandResult> ShowPackageAsync(
        string packageId,
        string packageSource
    )
    {
        return await SendCommandAsync(
            IpcHttpRoutes.Path("/packages/show"),
            new Dictionary<string, string>
            {
                ["packageId"] = packageId,
                ["packageSource"] = packageSource,
            }
        );
    }

    public async Task<int> GetVersionAsync()
    {
        return (await GetStatusAsync()).BuildNumber;
    }

    public async Task<IReadOnlyList<IpcPackageInfo>> SearchPackagesAsync(
        string query,
        string? managerName = null,
        int? maxResults = null
    )
    {
        Dictionary<string, string> parameters = new() { ["query"] = query };
        if (!string.IsNullOrWhiteSpace(managerName))
        {
            parameters["manager"] = managerName;
        }

        if (maxResults.HasValue)
        {
            parameters["maxResults"] = maxResults.Value.ToString();
        }

        return await ReadAuthenticatedJsonAsync<IReadOnlyList<IpcPackageInfo>>(
            HttpMethod.Get,
            IpcHttpRoutes.Path("/packages/search"),
            parameters
        ) ?? [];
    }

    public async Task<IReadOnlyList<IpcPackageInfo>> ListInstalledPackagesAsync(
        string? managerName = null
    )
    {
        Dictionary<string, string>? parameters = null;
        if (!string.IsNullOrWhiteSpace(managerName))
        {
            parameters = new Dictionary<string, string> { ["manager"] = managerName };
        }

        return await ReadAuthenticatedJsonAsync<IReadOnlyList<IpcPackageInfo>>(
            HttpMethod.Get,
            IpcHttpRoutes.Path("/packages/installed"),
            parameters
        ) ?? [];
    }

    public async Task<IReadOnlyList<IpcPackageInfo>> ListUpgradablePackagesAsync(
        string? managerName = null
    )
    {
        Dictionary<string, string>? parameters = null;
        if (!string.IsNullOrWhiteSpace(managerName))
        {
            parameters = new Dictionary<string, string> { ["manager"] = managerName };
        }

        return await ReadAuthenticatedJsonAsync<IReadOnlyList<IpcPackageInfo>>(
            HttpMethod.Get,
            IpcHttpRoutes.Path("/packages/updates"),
            parameters
        ) ?? [];
    }

    public async Task<IpcPackageDetailsInfo?> GetPackageDetailsAsync(
        IpcPackageActionRequest request
    )
    {
        return await ReadAuthenticatedJsonAsync<IpcPackageDetailsInfo>(
            HttpMethod.Get,
            IpcHttpRoutes.Path("/packages/details"),
            BuildPackageQueryParameters(request)
        );
    }

    public async Task<IReadOnlyList<string>> GetPackageVersionsAsync(
        IpcPackageActionRequest request
    )
    {
        return await ReadAuthenticatedJsonAsync<IReadOnlyList<string>>(
            HttpMethod.Get,
            IpcHttpRoutes.Path("/packages/versions"),
            BuildPackageQueryParameters(request)
        ) ?? [];
    }

    public async Task<IReadOnlyList<IpcIgnoredUpdateInfo>> ListIgnoredUpdatesAsync()
    {
        return await ReadAuthenticatedJsonAsync<IReadOnlyList<IpcIgnoredUpdateInfo>>(
            HttpMethod.Get,
            IpcHttpRoutes.Path("/packages/ignored")
        ) ?? [];
    }

    public async Task<IpcCommandResult> IgnorePackageUpdateAsync(
        IpcPackageActionRequest request
    )
    {
        return await SendCommandAsync(
            IpcHttpRoutes.Path("/packages/ignore"),
            BuildPackageQueryParameters(request)
        );
    }

    public async Task<IpcCommandResult> RemoveIgnoredUpdateAsync(
        IpcPackageActionRequest request
    )
    {
        return await SendCommandAsync(
            IpcHttpRoutes.Path("/packages/unignore"),
            BuildPackageQueryParameters(request)
        );
    }

    public async Task<IpcCommandResult> UpdateAllAsync()
    {
        return await SendCommandAsync(IpcHttpRoutes.Path("/packages/update-all"));
    }

    public async Task<IpcCommandResult> UpdateManagerAsync(string managerName)
    {
        return await SendCommandAsync(
            IpcHttpRoutes.Path("/packages/update-manager"),
            new Dictionary<string, string> { ["manager"] = managerName }
        );
    }

    public async Task<IpcPackageOperationResult> InstallPackageAsync(
        IpcPackageActionRequest request
    )
    {
        return await SendPackageOperationAsync(IpcHttpRoutes.Path("/packages/install"), request);
    }

    public async Task<IpcPackageOperationResult> DownloadPackageAsync(
        IpcPackageActionRequest request
    )
    {
        return await SendPackageOperationAsync(
            IpcHttpRoutes.Path("/packages/download"),
            request
        );
    }

    public async Task<IpcPackageOperationResult> ReinstallPackageAsync(
        IpcPackageActionRequest request
    )
    {
        return await SendPackageOperationAsync(
            IpcHttpRoutes.Path("/packages/reinstall"),
            request
        );
    }

    public async Task<IpcPackageOperationResult> UpdatePackageAsync(
        IpcPackageActionRequest request
    )
    {
        return await SendPackageOperationAsync(IpcHttpRoutes.Path("/packages/update"), request);
    }

    public async Task<IpcPackageOperationResult> UninstallPackageAsync(
        IpcPackageActionRequest request
    )
    {
        return await SendPackageOperationAsync(
            IpcHttpRoutes.Path("/packages/uninstall"),
            request
        );
    }

    public async Task<IpcPackageOperationResult> UninstallThenReinstallPackageAsync(
        IpcPackageActionRequest request
    )
    {
        return await SendPackageOperationAsync(
            IpcHttpRoutes.Path("/packages/uninstall-then-reinstall"),
            request
        );
    }

    private async Task<string> SendAuthenticatedAsync(
        HttpMethod method,
        string relativePath,
        IReadOnlyDictionary<string, string>? queryParameters = null,
        HttpContent? requestContent = null
    )
    {
        EnsureTokenAvailable();

        Dictionary<string, string> parameters = new(queryParameters ?? new Dictionary<string, string>())
        {
            ["token"] = _token,
        };

        return await SendAsync(method, relativePath, parameters, requestContent);
    }

    private async Task<string> SendAsync(
        HttpMethod method,
        string relativePath,
        IReadOnlyDictionary<string, string>? queryParameters = null,
        HttpContent? requestContent = null
    )
    {
        using var timeout = new CancellationTokenSource(GetRequestTimeout(method, relativePath));
        using var request = new HttpRequestMessage(method, BuildRelativeUri(relativePath, queryParameters));
        request.Content = requestContent;
        using var response = await _httpClient.SendAsync(request, timeout.Token);
        string content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(content) ? response.ReasonPhrase : content
            );
        }

        return content;
    }

    private async Task<T?> ReadAuthenticatedJsonAsync<T>(
        HttpMethod method,
        string relativePath,
        IReadOnlyDictionary<string, string>? queryParameters = null
    )
    {
        string json = await SendAuthenticatedAsync(method, relativePath, queryParameters);
        return IpcJson.Deserialize<T>(json);
    }

    private async Task<TResponse?> ReadAuthenticatedJsonWithBodyAsync<TResponse, TBody>(
        string relativePath,
        TBody body,
        IReadOnlyDictionary<string, string>? queryParameters = null
    )
    {
        using var content = IpcJson.CreateContent(body);
        string json = await SendAuthenticatedAsync(HttpMethod.Post, relativePath, queryParameters, content);
        return IpcJson.Deserialize<TResponse>(json);
    }

    private async Task<IpcPackageOperationResult> SendPackageOperationAsync(
        string relativePath,
        IpcPackageActionRequest request
    )
    {
        return await ReadAuthenticatedJsonAsync<IpcPackageOperationResult>(
                HttpMethod.Post,
                relativePath,
                BuildPackageQueryParameters(request)
            )
            ?? new IpcPackageOperationResult
            {
                Status = "error",
                Message = "The IPC API returned an empty response.",
            };
    }

    private async Task<IpcSourceOperationResult> SendSourceOperationAsync(
        string relativePath,
        IpcSourceRequest request
    )
    {
        Dictionary<string, string> parameters = new()
        {
            ["manager"] = request.ManagerName,
            ["name"] = request.SourceName,
        };

        if (!string.IsNullOrWhiteSpace(request.SourceUrl))
        {
            parameters["url"] = request.SourceUrl;
        }

        return await ReadAuthenticatedJsonAsync<IpcSourceOperationResult>(
                HttpMethod.Post,
                relativePath,
                parameters
            )
            ?? new IpcSourceOperationResult
            {
                Status = "error",
                Message = "The IPC API returned an empty response.",
            };
    }

    private async Task<IpcDesktopShortcutOperationResult> SendDesktopShortcutOperationAsync(
        string relativePath,
        IpcDesktopShortcutRequest request
    )
    {
        Dictionary<string, string> parameters = new() { ["path"] = request.Path };
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            parameters["status"] = request.Status;
        }

        return await ReadAuthenticatedJsonAsync<IpcDesktopShortcutOperationResult>(
                HttpMethod.Post,
                relativePath,
                parameters
            )
            ?? new IpcDesktopShortcutOperationResult
            {
                Status = "error",
                Message = "The IPC API returned an empty response.",
            };
    }

    private async Task<IpcManagerMaintenanceActionResult> SendManagerMaintenanceActionAsync(
        string relativePath,
        IpcManagerMaintenanceRequest request
    )
    {
        return await ReadAuthenticatedJsonWithBodyAsync<
                IpcManagerMaintenanceActionResult,
                IpcManagerMaintenanceRequest
            >(relativePath, request)
            ?? new IpcManagerMaintenanceActionResult
            {
                Status = "error",
                Message = "The IPC API returned an empty response.",
            };
    }

    private async Task<IpcCommandResult> SendCommandAsync(
        string relativePath,
        IReadOnlyDictionary<string, string>? queryParameters = null
    )
    {
        return await ReadAuthenticatedJsonAsync<IpcCommandResult>(
                HttpMethod.Post,
                relativePath,
                queryParameters
            )
            ?? new IpcCommandResult
            {
                Status = "error",
                Message = "The IPC API returned an empty response.",
            };
    }

    private static Dictionary<string, string> BuildPackageQueryParameters(
        IpcPackageActionRequest request
    )
    {
        Dictionary<string, string> parameters = new() { ["packageId"] = request.PackageId };

        if (!string.IsNullOrWhiteSpace(request.ManagerName))
        {
            parameters["manager"] = request.ManagerName;
        }

        if (!string.IsNullOrWhiteSpace(request.PackageSource))
        {
            parameters["packageSource"] = request.PackageSource;
        }

        if (!string.IsNullOrWhiteSpace(request.Version))
        {
            parameters["version"] = request.Version;
        }

        if (!string.IsNullOrWhiteSpace(request.Scope))
        {
            parameters["scope"] = request.Scope;
        }

        if (request.PreRelease.HasValue)
        {
            parameters["preRelease"] = request.PreRelease.Value ? "true" : "false";
        }

        if (request.Elevated.HasValue)
        {
            parameters["elevated"] = request.Elevated.Value ? "true" : "false";
        }

        if (request.Interactive.HasValue)
        {
            parameters["interactive"] = request.Interactive.Value ? "true" : "false";
        }

        if (request.SkipHash.HasValue)
        {
            parameters["skipHash"] = request.SkipHash.Value ? "true" : "false";
        }

        if (request.RemoveData.HasValue)
        {
            parameters["removeData"] = request.RemoveData.Value ? "true" : "false";
        }

        if (request.WaitForCompletion.HasValue)
        {
            parameters["wait"] = request.WaitForCompletion.Value ? "true" : "false";
        }

        if (!string.IsNullOrWhiteSpace(request.Architecture))
        {
            parameters["architecture"] = request.Architecture;
        }

        if (!string.IsNullOrWhiteSpace(request.InstallLocation))
        {
            parameters["location"] = request.InstallLocation;
        }

        if (!string.IsNullOrWhiteSpace(request.OutputPath))
        {
            parameters["outputPath"] = request.OutputPath;
        }

        return parameters;
    }

    private static Dictionary<string, string> BuildAppNavigateParameters(
        IpcAppNavigateRequest request
    )
    {
        Dictionary<string, string> parameters = new()
        {
            ["page"] = IpcAppPages.NormalizePageName(request.Page),
        };

        if (!string.IsNullOrWhiteSpace(request.ManagerName))
        {
            parameters["manager"] = request.ManagerName;
        }

        if (!string.IsNullOrWhiteSpace(request.HelpAttachment))
        {
            parameters["helpAttachment"] = request.HelpAttachment;
        }

        return parameters;
    }

    private static HttpClient CreateHttpClient(IpcTransportOptions options)
    {
        if (options.TransportKind == IpcTransportKind.NamedPipe)
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectCallback = async (_, cancellationToken) =>
                {
                    if (OperatingSystem.IsWindows())
                    {
                        var pipeClient = new NamedPipeClientStream(
                            ".",
                            options.NamedPipeName,
                            PipeDirection.InOut,
                            PipeOptions.Asynchronous
                        );
                        await pipeClient.ConnectAsync(cancellationToken);
                        return pipeClient;
                    }

                    string socketPath = options.NamedPipePath
                        ?? throw new InvalidOperationException(
                            "The Unix socket path is not available for the named-pipe transport."
                        );
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(
                        new UnixDomainSocketEndPoint(socketPath),
                        cancellationToken
                    );
                    return new NetworkStream(socket, ownsSocket: true);
                },
            };

            return new HttpClient(handler)
            {
                BaseAddress = options.BaseAddress,
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }

        return new HttpClient
        {
            BaseAddress = options.BaseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static TimeSpan GetRequestTimeout(HttpMethod method, string relativePath)
    {
        if (IpcHttpRoutes.Matches(relativePath, "/status"))
        {
            return TimeSpan.FromSeconds(5);
        }

        if (IpcHttpRoutes.StartsWith(relativePath, "/packages/"))
        {
            return method == HttpMethod.Post
                ? TimeSpan.FromMinutes(5)
                : TimeSpan.FromSeconds(30);
        }

        if (IpcHttpRoutes.StartsWith(relativePath, "/bundles/install"))
        {
            return TimeSpan.FromMinutes(5);
        }

        if (IpcHttpRoutes.StartsWith(relativePath, "/managers/maintenance/action"))
        {
            return TimeSpan.FromMinutes(10);
        }

        if (IpcHttpRoutes.StartsWith(relativePath, "/backups/github/sign-in/complete"))
        {
            return TimeSpan.FromMinutes(5);
        }

        if (
            IpcHttpRoutes.StartsWith(relativePath, "/backups/local/create")
            || IpcHttpRoutes.StartsWith(relativePath, "/backups/cloud/create")
        )
        {
            return TimeSpan.FromMinutes(2);
        }

        if (IpcHttpRoutes.StartsWith(relativePath, "/backups/"))
        {
            return method == HttpMethod.Post ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(30);
        }

        if (IpcHttpRoutes.StartsWith(relativePath, "/bundles/"))
        {
            return method == HttpMethod.Post ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(15);
        }

        if (IpcHttpRoutes.StartsWith(relativePath, "/sources/"))
        {
            return method == HttpMethod.Post
                ? TimeSpan.FromMinutes(2)
                : TimeSpan.FromSeconds(30);
        }

        if (
            IpcHttpRoutes.StartsWith(relativePath, "/managers")
            || IpcHttpRoutes.StartsWith(relativePath, "/settings")
            || IpcHttpRoutes.StartsWith(relativePath, "/secure-settings")
            || IpcHttpRoutes.StartsWith(relativePath, "/desktop-shortcuts")
            || IpcHttpRoutes.StartsWith(relativePath, "/logs/")
        )
        {
            return TimeSpan.FromSeconds(15);
        }

        return TimeSpan.FromSeconds(5);
    }

    private static bool IsConnectivityException(Exception exception)
    {
        return exception is HttpRequestException
            or IOException
            or TaskCanceledException
            or OperationCanceledException;
    }

    private static IpcEndpointRegistration? SelectLiveRegistration(
        IEnumerable<IpcEndpointRegistration> candidates
    )
    {
        foreach (IpcEndpointRegistration candidate in candidates)
        {
            if (candidate.ProcessId > 0 && !IsProcessRunning(candidate.ProcessId))
            {
                IpcTransportOptions.DeletePersistedMetadata(candidate.SessionId);
                continue;
            }

            if (IsEndpointAlive(candidate))
            {
                return candidate;
            }

            if (candidate.ProcessId <= 0)
            {
                IpcTransportOptions.DeletePersistedMetadata(candidate.SessionId);
            }
        }

        return null;
    }

    private static bool IsEndpointAlive(IpcEndpointRegistration candidate)
    {
        try
        {
            using var client = new IpcClient(candidate.ToTransportOptions(), candidate.Token);
            return client.GetStatusAsync().GetAwaiter().GetResult().Running;
        }
        catch (Exception ex) when (IsConnectivityException(ex) || ex is InvalidOperationException)
        {
            Logger.Debug(
                $"IPC API registration {candidate.SessionId} probe failed: {ex.Message}"
            );
            return false;
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void EnsureTokenAvailable()
    {
        if (string.IsNullOrWhiteSpace(_token))
        {
            throw new InvalidOperationException(
                "The IPC API token is not available. Start UniGetUI and try again."
            );
        }
    }

    private static string BuildRelativeUri(
        string relativePath,
        IReadOnlyDictionary<string, string>? queryParameters
    )
    {
        if (queryParameters is null || queryParameters.Count == 0)
        {
            return relativePath;
        }

        string query = string.Join(
            "&",
            queryParameters.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"
            )
        );

        return $"{relativePath}?{query}";
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

public sealed class IpcCommandResult
{
    public string Status { get; set; } = "success";
    public string Command { get; set; } = "";
    public string? Message { get; set; }

    public static IpcCommandResult Success(string command)
    {
        return new IpcCommandResult { Command = command };
    }
}
