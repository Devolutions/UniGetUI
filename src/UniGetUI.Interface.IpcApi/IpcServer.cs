using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UniGetUI.Core.Data;
using UniGetUI.Core.IconEngine;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Interface
{
    internal static class ApiTokenHolder
    {
        public static string Token = "";
    }

    public class IpcServer
    {
        public string SessionId { get; } = Guid.NewGuid().ToString("N");
        public string SessionKind { get; init; } = IpcTransportOptions.GuiSessionKind;
        public event EventHandler<EventArgs>? OnUpgradeAll;
        public event EventHandler<string>? OnUpgradeAllForManager;
        public Func<IpcAppInfo>? AppInfoProvider;
        public Func<IpcCommandResult>? ShowAppHandler;
        public Func<IpcAppNavigateRequest, IpcCommandResult>? NavigateAppHandler;
        public Func<IpcCommandResult>? QuitAppHandler;
        public Func<IpcPackageActionRequest, IpcCommandResult>? ShowPackageHandler;

        private IHost? _host;
        private IpcTransportOptions _transportOptions =
            IpcTransportOptions.LoadForServer();
        private string? _namedPipePath;
        private int _stopRequested;

        public IpcServer() { }

        public static bool AuthenticateToken(string? token)
        {
            return token == ApiTokenHolder.Token;
        }

        public async Task Start()
        {
            _transportOptions = IpcTransportOptions.LoadForServer();
            _namedPipePath = _transportOptions.NamedPipePath;
            PrepareTransportEndpoint();
            ApiTokenHolder.Token = CoreTools.RandomString(64);
            Logger.Info("Generated a IPC API auth token for the current session");

            var builder = Host.CreateDefaultBuilder();
            builder.ConfigureServices(services => services.AddCors());
            builder.ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseKestrel(serverOptions => ConfigureTransport(serverOptions));
#if !DEBUG
                webBuilder.SuppressStatusMessages(true);
#endif
                webBuilder.Configure(app =>
                {
                    app.UseCors(policy =>
                        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()
                    );

                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet(IpcHttpRoutes.Path("/status"), V3_Status);
                        endpoints.MapGet(IpcHttpRoutes.Path("/app"), V3_GetAppInfo);
                        endpoints.MapPost(IpcHttpRoutes.Path("/app/show"), V3_ShowApp);
                        endpoints.MapPost(IpcHttpRoutes.Path("/app/navigate"), V3_NavigateApp);
                        endpoints.MapPost(IpcHttpRoutes.Path("/app/quit"), V3_QuitApp);
                        endpoints.MapGet(IpcHttpRoutes.Path("/operations"), V3_ListOperations);
                        endpoints.MapGet(
                            IpcHttpRoutes.Path("/operations/{operationId}"),
                            V3_GetOperation
                        );
                        endpoints.MapGet(
                            IpcHttpRoutes.Path("/operations/{operationId}/output"),
                            V3_GetOperationOutput
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/operations/{operationId}/cancel"),
                            V3_CancelOperation
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/operations/{operationId}/retry"),
                            V3_RetryOperation
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/operations/{operationId}/reorder"),
                            V3_ReorderOperation
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/operations/{operationId}/forget"),
                            V3_ForgetOperation
                        );
                        endpoints.MapGet(IpcHttpRoutes.Path("/managers"), V3_ListManagers);
                        endpoints.MapGet(
                            IpcHttpRoutes.Path("/managers/maintenance"),
                            V3_GetManagerMaintenance
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/managers/maintenance/reload"),
                            V3_ReloadManager
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/managers/maintenance/executable/set"),
                            V3_SetManagerExecutable
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/managers/maintenance/executable/clear"),
                            V3_ClearManagerExecutable
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/managers/maintenance/action"),
                            V3_RunManagerAction
                        );
                        endpoints.MapGet(IpcHttpRoutes.Path("/sources"), V3_ListSources);
                        endpoints.MapPost(IpcHttpRoutes.Path("/sources/add"), V3_AddSource);
                        endpoints.MapPost(IpcHttpRoutes.Path("/sources/remove"), V3_RemoveSource);
                        endpoints.MapGet(IpcHttpRoutes.Path("/settings"), V3_ListSettings);
                        endpoints.MapGet(IpcHttpRoutes.Path("/settings/item"), V3_GetSetting);
                        endpoints.MapPost(IpcHttpRoutes.Path("/settings/set"), V3_SetSetting);
                        endpoints.MapPost(IpcHttpRoutes.Path("/settings/clear"), V3_ClearSetting);
                        endpoints.MapPost(IpcHttpRoutes.Path("/settings/reset"), V3_ResetSettings);
                        endpoints.MapGet(
                            IpcHttpRoutes.Path("/secure-settings"),
                            V3_ListSecureSettings
                        );
                        endpoints.MapGet(
                            IpcHttpRoutes.Path("/secure-settings/item"),
                            V3_GetSecureSetting
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/secure-settings/set"),
                            V3_SetSecureSetting
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/managers/set-enabled"),
                            V3_SetManagerEnabled
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/managers/set-update-notifications"),
                            V3_SetManagerUpdateNotifications
                        );
                        endpoints.MapGet(
                            IpcHttpRoutes.Path("/desktop-shortcuts"),
                            V3_ListDesktopShortcuts
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/desktop-shortcuts/set"),
                            V3_SetDesktopShortcut
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/desktop-shortcuts/reset"),
                            V3_ResetDesktopShortcut
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/desktop-shortcuts/reset-all"),
                            V3_ResetDesktopShortcuts
                        );
                        endpoints.MapGet(IpcHttpRoutes.Path("/logs/app"), V3_GetAppLog);
                        endpoints.MapGet(
                            IpcHttpRoutes.Path("/logs/history"),
                            V3_GetOperationHistory
                        );
                        endpoints.MapGet(IpcHttpRoutes.Path("/logs/manager"), V3_GetManagerLog);
                        endpoints.MapGet(IpcHttpRoutes.Path("/backups/status"), V3_GetBackupStatus);
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/backups/local/create"),
                            V3_CreateLocalBackup
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/backups/github/sign-in/start"),
                            V3_StartGitHubDeviceFlow
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/backups/github/sign-in/complete"),
                            V3_CompleteGitHubDeviceFlow
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/backups/github/sign-out"),
                            V3_SignOutGitHub
                        );
                        endpoints.MapGet(
                            IpcHttpRoutes.Path("/backups/cloud"),
                            V3_ListCloudBackups
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/backups/cloud/create"),
                            V3_CreateCloudBackup
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/backups/cloud/download"),
                            V3_DownloadCloudBackup
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/backups/cloud/restore"),
                            V3_RestoreCloudBackup
                        );
                        endpoints.MapGet(IpcHttpRoutes.Path("/bundles"), V3_GetBundle);
                        endpoints.MapPost(IpcHttpRoutes.Path("/bundles/reset"), V3_ResetBundle);
                        endpoints.MapPost(IpcHttpRoutes.Path("/bundles/import"), V3_ImportBundle);
                        endpoints.MapPost(IpcHttpRoutes.Path("/bundles/export"), V3_ExportBundle);
                        endpoints.MapPost(IpcHttpRoutes.Path("/bundles/add"), V3_AddBundlePackage);
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/bundles/remove"),
                            V3_RemoveBundlePackage
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/bundles/install"),
                            V3_InstallBundle
                        );
                        endpoints.MapGet(
                            IpcHttpRoutes.Path("/packages/search"),
                            V3_SearchPackages
                        );
                        endpoints.MapGet(
                            IpcHttpRoutes.Path("/packages/installed"),
                            V3_ListInstalledPackages
                        );
                        endpoints.MapGet(
                            IpcHttpRoutes.Path("/packages/updates"),
                            V3_ListUpgradablePackages
                        );
                        endpoints.MapGet(
                            IpcHttpRoutes.Path("/packages/details"),
                            V3_GetPackageDetails
                        );
                        endpoints.MapGet(
                            IpcHttpRoutes.Path("/packages/versions"),
                            V3_GetPackageVersions
                        );
                        endpoints.MapGet(
                            IpcHttpRoutes.Path("/packages/ignored"),
                            V3_ListIgnoredUpdates
                        );
                        endpoints.MapPost(IpcHttpRoutes.Path("/packages/ignore"), V3_IgnorePackage);
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/packages/unignore"),
                            V3_UnignorePackage
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/packages/download"),
                            V3_DownloadPackage
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/packages/install"),
                            V3_InstallPackage
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/packages/reinstall"),
                            V3_ReinstallPackage
                        );
                        endpoints.MapPost(IpcHttpRoutes.Path("/packages/update"), V3_UpdatePackage);
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/packages/uninstall"),
                            V3_UninstallPackage
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/packages/uninstall-then-reinstall"),
                            V3_UninstallThenReinstallPackage
                        );
                        endpoints.MapPost(IpcHttpRoutes.Path("/packages/show"), V3_ShowPackage);
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/packages/update-all"),
                            V3_UpdateAllPackages
                        );
                        endpoints.MapPost(
                            IpcHttpRoutes.Path("/packages/update-manager"),
                            V3_UpdateAllPackagesForManager
                        );
                    });
                });
            });
            _host = builder.Build();
            try
            {
                await _host.StartAsync();
                ApplyTransportSecurity();
                _transportOptions.Persist(
                    SessionId,
                    ApiTokenHolder.Token,
                    SessionKind,
                    Environment.ProcessId
                );
            }
            catch
            {
                IpcTransportOptions.DeletePersistedMetadata(SessionId);
                CleanupTransportEndpoint();
                _host.Dispose();
                _host = null;
                throw;
            }
            Logger.Info(
                _transportOptions.TransportKind == IpcTransportKind.NamedPipe
                    ? OperatingSystem.IsWindows()
                        ? $"Api running on named pipe {_transportOptions.NamedPipeName}"
                        : $"Api running on unix socket {_transportOptions.NamedPipeDisplayName}"
                    : $"Api running on {_transportOptions.BaseAddressString}"
            );
        }

        private void ConfigureTransport(KestrelServerOptions serverOptions)
        {
            if (_transportOptions.TransportKind == IpcTransportKind.NamedPipe)
            {
                if (OperatingSystem.IsWindows())
                {
                    serverOptions.ListenNamedPipe(
                        _transportOptions.NamedPipeName,
                        listenOptions =>
                        {
                            listenOptions.Protocols = HttpProtocols.Http1;
                        }
                    );
                }
                else
                {
                    serverOptions.ListenUnixSocket(
                        _namedPipePath
                            ?? throw new InvalidOperationException(
                                "The Unix socket path is not available for the current transport."
                            ),
                        listenOptions =>
                        {
                            listenOptions.Protocols = HttpProtocols.Http1;
                        }
                    );
                }
            }
            else
            {
                serverOptions.ListenLocalhost(_transportOptions.TcpPort);
            }
        }

        private void PrepareTransportEndpoint()
        {
            if (_transportOptions.TransportKind != IpcTransportKind.NamedPipe
                || OperatingSystem.IsWindows())
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_namedPipePath))
            {
                throw new InvalidOperationException(
                    "The Unix socket path is required for the named-pipe transport."
                );
            }

            string? directory = Path.GetDirectoryName(_namedPipePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(_namedPipePath))
            {
                if (HasExplicitUnixSocketPath())
                {
                    throw new InvalidOperationException(
                        $"Cannot bind the IPC API Unix socket because the explicit path \"{_namedPipePath}\" already exists."
                    );
                }

                DeleteUnixSocketFile(_namedPipePath);
            }
        }

        private void CleanupTransportEndpoint()
        {
            if (_transportOptions.TransportKind != IpcTransportKind.NamedPipe
                || OperatingSystem.IsWindows()
                || string.IsNullOrWhiteSpace(_namedPipePath))
            {
                return;
            }

            DeleteUnixSocketFile(_namedPipePath);
        }

        private void ApplyTransportSecurity()
        {
            if (_transportOptions.TransportKind != IpcTransportKind.NamedPipe
                || OperatingSystem.IsWindows()
                || string.IsNullOrWhiteSpace(_namedPipePath))
            {
                return;
            }

            if (!File.Exists(_namedPipePath))
            {
                throw new InvalidOperationException(
                    $"The IPC API Unix socket \"{_namedPipePath}\" was not created."
                );
            }

            File.SetUnixFileMode(
                _namedPipePath,
                IpcTransportOptions.SameUserUnixSocketMode
            );
        }

        private bool HasExplicitUnixSocketPath()
        {
            return _transportOptions.NamedPipeName.StartsWith("/", StringComparison.Ordinal);
        }

        private static void DeleteUnixSocketFile(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private async Task V3_Status(HttpContext context)
        {
            await context.Response.WriteAsJsonAsync(
                new IpcStatus
                {
                    Running = true,
                    Transport = _transportOptions.TransportKind switch
                    {
                        IpcTransportKind.NamedPipe => "named-pipe",
                        _ => "tcp",
                    },
                    TcpPort = _transportOptions.TcpPort,
                    NamedPipeName = _transportOptions.NamedPipeName,
                    NamedPipePath = _transportOptions.NamedPipePath ?? "",
                    BaseAddress = _transportOptions.BaseAddressString,
                    Version = CoreData.VersionName,
                    BuildNumber = CoreData.BuildNumber,
                },
                new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                }
            );
        }

        private async Task V3_ListManagers(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            await context.Response.WriteAsJsonAsync(
                IpcManagerSettingsApi.ListManagers(),
                new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                }
            );
        }

        private async Task V3_GetAppInfo(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    AppInfoProvider?.Invoke()
                        ?? throw new InvalidOperationException(
                            "The application did not register an app-state provider."
                        ),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_ShowApp(HttpContext context)
        {
            await HandleCommandAsync(
                context,
                () =>
                    ShowAppHandler?.Invoke()
                    ?? throw new InvalidOperationException(
                        "The current UniGetUI session cannot show a window."
                    )
            );
        }

        private async Task V3_NavigateApp(HttpContext context)
        {
            await HandleCommandAsync(
                context,
                () =>
                    NavigateAppHandler?.Invoke(BuildAppNavigateRequest(context.Request))
                    ?? throw new InvalidOperationException(
                        "The current UniGetUI session cannot navigate application pages."
                    )
            );
        }

        private async Task V3_QuitApp(HttpContext context)
        {
            await HandleCommandAsync(
                context,
                () =>
                    QuitAppHandler?.Invoke()
                    ?? throw new InvalidOperationException(
                        "The current UniGetUI session cannot be shut down through automation."
                    )
            );
        }

        private async Task V3_ListOperations(HttpContext context)
        {
            await HandleReadAsync(context, IpcOperationApi.ListOperations);
        }

        private async Task V3_GetOperation(HttpContext context)
        {
            await HandleReadAsync(
                context,
                () => IpcOperationApi.GetOperation(GetRequiredRouteValue(context, "operationId"))
            );
        }

        private async Task V3_GetOperationOutput(HttpContext context)
        {
            await HandleReadAsync(
                context,
                () => IpcOperationApi.GetOperationOutput(
                    GetRequiredRouteValue(context, "operationId"),
                    int.TryParse(context.Request.Query["tailLines"], out int tailLines)
                        ? tailLines
                        : null
                )
            );
        }

        private async Task V3_CancelOperation(HttpContext context)
        {
            await HandleCommandAsync(
                context,
                () => IpcOperationApi.CancelOperation(GetRequiredRouteValue(context, "operationId"))
            );
        }

        private async Task V3_RetryOperation(HttpContext context)
        {
            await HandleCommandAsync(
                context,
                () => IpcOperationApi.RetryOperation(
                    GetRequiredRouteValue(context, "operationId"),
                    context.Request.Query.TryGetValue("mode", out var mode) ? mode.ToString() : null
                )
            );
        }

        private async Task V3_ReorderOperation(HttpContext context)
        {
            await HandleCommandAsync(
                context,
                () => IpcOperationApi.ReorderOperation(
                    GetRequiredRouteValue(context, "operationId"),
                    GetRequiredQueryValue(context, "action")
                )
            );
        }

        private async Task V3_ForgetOperation(HttpContext context)
        {
            await HandleCommandAsync(
                context,
                () => IpcOperationApi.ForgetOperation(GetRequiredRouteValue(context, "operationId"))
            );
        }

        private async Task V3_ListSources(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    IpcManagerSettingsApi.ListSources(context.Request.Query["manager"]),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_GetManagerMaintenance(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            string managerName = context.Request.Query["manager"].ToString();
            if (string.IsNullOrWhiteSpace(managerName))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("The manager parameter is required.");
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    IpcManagerMaintenanceApi.GetMaintenanceInfo(managerName),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_ReloadManager(HttpContext context)
        {
            await HandleManagerMaintenanceActionAsync<
                IpcManagerMaintenanceRequest,
                IpcManagerMaintenanceActionResult
            >(context, IpcManagerMaintenanceApi.ReloadManagerAsync);
        }

        private async Task V3_SetManagerExecutable(HttpContext context)
        {
            await HandleManagerMaintenanceActionAsync<
                IpcManagerMaintenanceRequest,
                IpcManagerMaintenanceActionResult
            >(context, IpcManagerMaintenanceApi.SetExecutablePathAsync);
        }

        private async Task V3_ClearManagerExecutable(HttpContext context)
        {
            await HandleManagerMaintenanceActionAsync<
                IpcManagerMaintenanceRequest,
                IpcManagerMaintenanceActionResult
            >(context, IpcManagerMaintenanceApi.ClearExecutablePathAsync);
        }

        private async Task V3_RunManagerAction(HttpContext context)
        {
            await HandleManagerMaintenanceActionAsync<
                IpcManagerMaintenanceRequest,
                IpcManagerMaintenanceActionResult
            >(context, IpcManagerMaintenanceApi.RunActionAsync);
        }

        private async Task V3_AddSource(HttpContext context)
        {
            await HandleSourceActionAsync(context, IpcManagerSettingsApi.AddSourceAsync);
        }

        private async Task V3_RemoveSource(HttpContext context)
        {
            await HandleSourceActionAsync(context, IpcManagerSettingsApi.RemoveSourceAsync);
        }

        private async Task V3_ListSettings(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            await context.Response.WriteAsJsonAsync(
                IpcManagerSettingsApi.ListSettings(),
                new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                }
            );
        }

        private async Task V3_GetSetting(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            string key = context.Request.Query["key"].ToString();
            if (string.IsNullOrWhiteSpace(key))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("The key parameter is required.");
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    IpcManagerSettingsApi.GetSetting(key),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_SetSetting(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    IpcManagerSettingsApi.SetSetting(
                        new IpcSettingValueRequest
                        {
                            SettingKey = GetRequiredQueryValue(context, "key"),
                            Enabled = bool.TryParse(context.Request.Query["enabled"], out bool enabled)
                                ? enabled
                                : null,
                            Value = GetOptionalQueryValue(context.Request, "value"),
                        }
                    ),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_ClearSetting(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            string key = context.Request.Query["key"].ToString();
            if (string.IsNullOrWhiteSpace(key))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("The key parameter is required.");
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    IpcManagerSettingsApi.ClearSetting(key),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_ResetSettings(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            IpcManagerSettingsApi.ResetSettingsPreservingSession();
            await context.Response.WriteAsJsonAsync(
                IpcCommandResult.Success("reset-settings"),
                new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                }
            );
        }

        private async Task V3_ListSecureSettings(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            await context.Response.WriteAsJsonAsync(
                IpcSecureSettingsApi.ListSettings(context.Request.Query["user"]),
                new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                }
            );
        }

        private async Task V3_GetSecureSetting(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    IpcSecureSettingsApi.GetSetting(
                        GetRequiredQueryValue(context, "key"),
                        GetOptionalQueryValue(context.Request, "user")
                    ),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_SetSecureSetting(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            if (!bool.TryParse(context.Request.Query["enabled"], out bool enabled))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("The enabled parameter must be either true or false.");
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    await IpcSecureSettingsApi.SetSettingAsync(
                        new IpcSecureSettingRequest
                        {
                            SettingKey = GetRequiredQueryValue(context, "key"),
                            UserName = GetOptionalQueryValue(context.Request, "user"),
                            Enabled = enabled,
                        }
                    ),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_SetManagerEnabled(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            if (!bool.TryParse(context.Request.Query["enabled"], out bool enabled))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("The enabled parameter must be either true or false.");
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    await IpcManagerSettingsApi.SetManagerEnabledAsync(
                        new IpcManagerToggleRequest
                        {
                            ManagerName = GetRequiredQueryValue(context, "manager"),
                            Enabled = enabled,
                        }
                    ),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_SetManagerUpdateNotifications(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            if (!bool.TryParse(context.Request.Query["enabled"], out bool enabled))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("The enabled parameter must be either true or false.");
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    IpcManagerSettingsApi.SetManagerNotifications(
                        new IpcManagerToggleRequest
                        {
                            ManagerName = GetRequiredQueryValue(context, "manager"),
                            Enabled = enabled,
                        }
                    ),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_ListDesktopShortcuts(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            await context.Response.WriteAsJsonAsync(
                IpcDesktopShortcutsApi.ListShortcuts(),
                new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                }
            );
        }

        private async Task V3_SetDesktopShortcut(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    IpcDesktopShortcutsApi.SetShortcut(
                        new IpcDesktopShortcutRequest
                        {
                            Path = GetRequiredQueryValue(context, "path"),
                            Status = GetOptionalQueryValue(context.Request, "status"),
                        }
                    ),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_ResetDesktopShortcut(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    IpcDesktopShortcutsApi.ResetShortcut(
                        new IpcDesktopShortcutRequest
                        {
                            Path = GetRequiredQueryValue(context, "path"),
                        }
                    ),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_ResetDesktopShortcuts(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            await context.Response.WriteAsJsonAsync(
                IpcDesktopShortcutsApi.ResetAllShortcuts(),
                new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                }
            );
        }

        private async Task V3_GetAppLog(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            int level = int.TryParse(context.Request.Query["level"], out int parsedLevel)
                ? parsedLevel
                : 4;
            await context.Response.WriteAsJsonAsync(
                IpcLogsApi.ListAppLog(level),
                new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                }
            );
        }

        private async Task V3_GetOperationHistory(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            await context.Response.WriteAsJsonAsync(
                IpcLogsApi.ListOperationHistory(),
                new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                }
            );
        }

        private async Task V3_GetManagerLog(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    IpcLogsApi.ListManagerLogs(
                        context.Request.Query["manager"],
                        bool.TryParse(context.Request.Query["verbose"], out bool verbose) && verbose
                    ),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_GetBackupStatus(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            await context.Response.WriteAsJsonAsync(
                await IpcBackupApi.GetStatusAsync(),
                new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                }
            );
        }

        private async Task V3_CreateLocalBackup(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    await IpcBackupApi.CreateLocalBackupAsync(),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_StartGitHubDeviceFlow(HttpContext context)
        {
            await HandleBackupActionAsync<
                IpcGitHubDeviceFlowRequest,
                IpcGitHubAuthResult
            >(context, IpcBackupApi.StartGitHubDeviceFlowAsync);
        }

        private async Task V3_CompleteGitHubDeviceFlow(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    await IpcBackupApi.CompleteGitHubDeviceFlowAsync(),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_SignOutGitHub(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            await context.Response.WriteAsJsonAsync(
                await IpcBackupApi.SignOutGitHubAsync(),
                new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                }
            );
        }

        private async Task V3_ListCloudBackups(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    await IpcBackupApi.ListCloudBackupsAsync(),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_CreateCloudBackup(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    await IpcBackupApi.CreateCloudBackupAsync(),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_DownloadCloudBackup(HttpContext context)
        {
            await HandleBackupActionAsync<
                IpcCloudBackupRequest,
                IpcCloudBackupContentResult
            >(context, IpcBackupApi.DownloadCloudBackupAsync);
        }

        private async Task V3_RestoreCloudBackup(HttpContext context)
        {
            await HandleBackupActionAsync<
                IpcCloudBackupRequest,
                IpcCloudBackupRestoreResult
            >(context, IpcBackupApi.RestoreCloudBackupAsync);
        }

        private async Task V3_GetBundle(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    await IpcBundleApi.GetCurrentBundleAsync(),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_ResetBundle(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    IpcBundleApi.ResetBundle(),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_ImportBundle(HttpContext context)
        {
            await HandleBundleActionAsync<IpcBundleImportRequest, IpcBundleImportResult>(
                context,
                IpcBundleApi.ImportBundleAsync
            );
        }

        private async Task V3_ExportBundle(HttpContext context)
        {
            await HandleBundleActionAsync<IpcBundleExportRequest, IpcBundleExportResult>(
                context,
                IpcBundleApi.ExportBundleAsync
            );
        }

        private async Task V3_AddBundlePackage(HttpContext context)
        {
            await HandleBundleActionAsync<
                IpcBundlePackageRequest,
                IpcBundlePackageOperationResult
            >(context, IpcBundleApi.AddPackageAsync);
        }

        private async Task V3_RemoveBundlePackage(HttpContext context)
        {
            await HandleBundleActionAsync<
                IpcBundlePackageRequest,
                IpcBundlePackageOperationResult
            >(context, IpcBundleApi.RemovePackageAsync);
        }

        private async Task V3_InstallBundle(HttpContext context)
        {
            await HandleBundleActionAsync<
                IpcBundleInstallRequest,
                IpcBundleInstallResult
            >(context, IpcBundleApi.InstallBundleAsync);
        }

        private async Task V3_SearchPackages(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            string query = context.Request.Query["query"].ToString();
            if (string.IsNullOrWhiteSpace(query))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("The query parameter is required.");
                return;
            }

            string? manager = context.Request.Query["manager"];
            int maxResults = 50;
            if (
                int.TryParse(context.Request.Query["maxResults"], out int parsedMaxResults)
                && parsedMaxResults > 0
            )
            {
                maxResults = parsedMaxResults;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    IpcPackageApi.SearchPackages(query, manager, maxResults),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_ListInstalledPackages(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    IpcPackageApi.ListInstalledPackages(context.Request.Query["manager"]),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_ListUpgradablePackages(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    IpcPackageApi.ListUpgradablePackages(context.Request.Query["manager"]),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_GetPackageDetails(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            string packageId = context.Request.Query["packageId"].ToString();
            if (string.IsNullOrWhiteSpace(packageId))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("The packageId parameter is required.");
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    await IpcPackageApi.GetPackageDetailsAsync(
                        BuildPackageActionRequest(context.Request)
                    ),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_GetPackageVersions(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            string packageId = context.Request.Query["packageId"].ToString();
            if (string.IsNullOrWhiteSpace(packageId))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("The packageId parameter is required.");
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    IpcPackageApi.GetPackageVersions(BuildPackageActionRequest(context.Request)),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_ListIgnoredUpdates(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            await context.Response.WriteAsJsonAsync(
                IpcPackageApi.ListIgnoredUpdates(),
                new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                }
            );
        }

        private async Task V3_IgnorePackage(HttpContext context)
        {
            await HandleCommandActionAsync(context, IpcPackageApi.IgnorePackageUpdateAsync);
        }

        private async Task V3_UnignorePackage(HttpContext context)
        {
            await HandleCommandActionAsync(context, IpcPackageApi.RemoveIgnoredUpdateAsync);
        }

        private async Task V3_InstallPackage(HttpContext context)
        {
            await HandlePackageActionAsync(
                context,
                IpcPackageApi.InstallPackageAsync
            );
        }

        private async Task V3_DownloadPackage(HttpContext context)
        {
            await HandlePackageActionAsync(
                context,
                IpcPackageApi.DownloadPackageAsync
            );
        }

        private async Task V3_ReinstallPackage(HttpContext context)
        {
            await HandlePackageActionAsync(
                context,
                IpcPackageApi.ReinstallPackageAsync
            );
        }

        private async Task V3_UpdatePackage(HttpContext context)
        {
            await HandlePackageActionAsync(
                context,
                IpcPackageApi.UpdatePackageAsync
            );
        }

        private async Task V3_UninstallPackage(HttpContext context)
        {
            await HandlePackageActionAsync(
                context,
                IpcPackageApi.UninstallPackageAsync
            );
        }

        private async Task V3_UninstallThenReinstallPackage(HttpContext context)
        {
            await HandlePackageActionAsync(
                context,
                IpcPackageApi.UninstallThenReinstallPackageAsync
            );
        }

        private async Task V3_ShowPackage(HttpContext context)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            string packageId = context.Request.Query["packageId"].ToString();
            if (string.IsNullOrWhiteSpace(packageId))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("The packageId parameter is required.");
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    ShowPackageHandler?.Invoke(BuildPackageActionRequest(context.Request))
                        ?? throw new InvalidOperationException(
                            "The current UniGetUI session cannot open package details."
                        ),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private async Task V3_UpdateAllPackages(HttpContext context)
        {
            await HandleCommandAsync(
                context,
                () =>
                {
                    if (OnUpgradeAll is null)
                    {
                        throw new InvalidOperationException(
                            "The current UniGetUI session cannot update all packages."
                        );
                    }

                    OnUpgradeAll.Invoke(null, EventArgs.Empty);
                    return IpcCommandResult.Success("update-all");
                }
            );
        }

        private async Task V3_UpdateAllPackagesForManager(HttpContext context)
        {
            await HandleCommandAsync(
                context,
                () =>
                {
                    if (OnUpgradeAllForManager is null)
                    {
                        throw new InvalidOperationException(
                            "The current UniGetUI session cannot update manager packages."
                        );
                    }

                    string managerName = GetRequiredQueryValue(context, "manager");
                    OnUpgradeAllForManager.Invoke(null, managerName);
                    return IpcCommandResult.Success("update-manager");
                }
            );
        }

        private static async Task HandlePackageActionAsync(
            HttpContext context,
            Func<IpcPackageActionRequest, Task<IpcPackageOperationResult>> action
        )
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            string packageId = context.Request.Query["packageId"].ToString();
            if (string.IsNullOrWhiteSpace(packageId))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("The packageId parameter is required.");
                return;
            }

            try
            {
                var request = BuildPackageActionRequest(context.Request);

                await context.Response.WriteAsJsonAsync(
                    await action(request),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private static async Task HandleReadAsync<T>(HttpContext context, Func<T> action)
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    action(),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private static async Task HandleCommandAsync(
            HttpContext context,
            Func<IpcCommandResult> action
        )
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    action(),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private static async Task HandleCommandActionAsync(
            HttpContext context,
            Func<IpcPackageActionRequest, Task<IpcCommandResult>> action
        )
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            string packageId = context.Request.Query["packageId"].ToString();
            if (string.IsNullOrWhiteSpace(packageId))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("The packageId parameter is required.");
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    await action(BuildPackageActionRequest(context.Request)),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private static async Task HandleSourceActionAsync(
            HttpContext context,
            Func<IpcSourceRequest, Task<IpcSourceOperationResult>> action
        )
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    await action(
                        new IpcSourceRequest
                        {
                            ManagerName = GetRequiredQueryValue(context, "manager"),
                            SourceName = GetRequiredQueryValue(context, "name"),
                            SourceUrl = GetOptionalQueryValue(context.Request, "url"),
                        }
                    ),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private static async Task HandleBundleActionAsync<TRequest, TResult>(
            HttpContext context,
            Func<TRequest, Task<TResult>> action
        )
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    await action(await ReadJsonBodyAsync<TRequest>(context)),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private static async Task HandleBackupActionAsync<TRequest, TResult>(
            HttpContext context,
            Func<TRequest, Task<TResult>> action
        )
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    await action(await ReadJsonBodyAsync<TRequest>(context)),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private static async Task HandleManagerMaintenanceActionAsync<TRequest, TResult>(
            HttpContext context,
            Func<TRequest, Task<TResult>> action
        )
        {
            if (!AuthenticateToken(context.Request.Query["token"]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            try
            {
                await context.Response.WriteAsJsonAsync(
                    await action(await ReadJsonBodyAsync<TRequest>(context)),
                    new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(ex.Message);
            }
        }

        private static async Task<TRequest> ReadJsonBodyAsync<TRequest>(HttpContext context)
        {
            var request = await context.Request.ReadFromJsonAsync<TRequest>(
                new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                }
            );
            return request
                ?? throw new InvalidOperationException("The request body is required.");
        }

        private static IpcPackageActionRequest BuildPackageActionRequest(HttpRequest request)
        {
            return new IpcPackageActionRequest
            {
                PackageId = GetRequiredQueryValue(request, "packageId"),
                ManagerName = GetOptionalQueryValue(request, "manager"),
                PackageSource = GetOptionalQueryValue(request, "packageSource"),
                Version = GetOptionalQueryValue(request, "version"),
                Scope = GetOptionalQueryValue(request, "scope"),
                PreRelease = bool.TryParse(request.Query["preRelease"], out bool preRelease)
                    ? preRelease
                    : null,
                Elevated = bool.TryParse(request.Query["elevated"], out bool elevated)
                    ? elevated
                    : null,
                Interactive = bool.TryParse(request.Query["interactive"], out bool interactive)
                    ? interactive
                    : null,
                SkipHash = bool.TryParse(request.Query["skipHash"], out bool skipHash)
                    ? skipHash
                    : null,
                RemoveData = bool.TryParse(request.Query["removeData"], out bool removeData)
                    ? removeData
                    : null,
                WaitForCompletion = bool.TryParse(request.Query["wait"], out bool waitForCompletion)
                    ? waitForCompletion
                    : null,
                Architecture = GetOptionalQueryValue(request, "architecture"),
                InstallLocation = GetOptionalQueryValue(request, "location"),
                OutputPath = GetOptionalQueryValue(request, "outputPath"),
            };
        }

        private static IpcAppNavigateRequest BuildAppNavigateRequest(HttpRequest request)
        {
            return new IpcAppNavigateRequest
            {
                Page = GetRequiredQueryValue(request, "page"),
                ManagerName = GetOptionalQueryValue(request, "manager"),
                HelpAttachment = GetOptionalQueryValue(request, "helpAttachment"),
            };
        }

        private static string GetRequiredRouteValue(HttpContext context, string key)
        {
            return context.Request.RouteValues.TryGetValue(key, out object? value)
                && value is string stringValue
                && !string.IsNullOrWhiteSpace(stringValue)
                ? stringValue
                : throw new InvalidOperationException($"The route value \"{key}\" is required.");
        }

        private static string GetRequiredQueryValue(HttpContext context, string key)
        {
            string? value = context.Request.Query[key].ToString();
            return !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new InvalidOperationException($"The query value \"{key}\" is required.");
        }

        private static string GetRequiredQueryValue(HttpRequest request, string key)
        {
            string? value = request.Query[key].ToString();
            return !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new InvalidOperationException($"The query value \"{key}\" is required.");
        }

        private static string? GetOptionalQueryValue(HttpRequest request, string key)
        {
            if (!request.Query.TryGetValue(key, out var value))
            {
                return null;
            }

            string? stringValue = value.ToString();
            return string.IsNullOrWhiteSpace(stringValue) ? null : stringValue;
        }

        public async Task Stop()
        {
            if (Interlocked.Exchange(ref _stopRequested, 1) == 1)
            {
                return;
            }

            try
            {
                if (_host is not null)
                {
                    await _host.StopAsync().ConfigureAwait(false);
                    _host.Dispose();
                    _host = null;
                }

                Logger.Info("Api was shut down");
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
            finally
            {
                CleanupTransportEndpoint();
                IpcTransportOptions.DeletePersistedMetadata(SessionId);
            }
        }
    }
}
