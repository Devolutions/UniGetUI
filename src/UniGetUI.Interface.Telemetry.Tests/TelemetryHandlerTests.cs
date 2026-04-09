using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.PackageClasses;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace UniGetUI.Interface.Telemetry.Tests;

public sealed class TelemetryHandlerTests : IDisposable
{
    private const string KnownInstallId =
        "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";

    private readonly string _testRoot;
    private readonly string _portableMarkerPath;
    private readonly bool _originalWasDaemon;

    public TelemetryHandlerTests()
    {
        _testRoot = Path.Combine(
            AppContext.BaseDirectory,
            nameof(TelemetryHandlerTests),
            Guid.NewGuid().ToString("N")
        );
        _portableMarkerPath = Path.Combine(Environment.CurrentDirectory, "ForceUniGetUIPortable");
        _originalWasDaemon = CoreData.WasDaemon;

        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);

        ClearSettingsCaches();
        Settings.ResetSettings();
        Settings.Set(Settings.K.DisableTelemetry, false);
        Settings.Set(Settings.K.DisableWaitForInternetConnection, true);
        Settings.SetValue(Settings.K.TelemetryClientToken, KnownInstallId);

        TelemetryHandler.ResetTestState();
        File.Delete(_portableMarkerPath);
        CoreData.WasDaemon = false;
    }

    public void Dispose()
    {
        TelemetryHandler.ResetTestState();
        ClearSettingsCaches();
        CoreData.TEST_DataDirectoryOverride = null;
        CoreData.WasDaemon = _originalWasDaemon;

        if (File.Exists(_portableMarkerPath))
        {
            File.Delete(_portableMarkerPath);
        }

        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    [Fact]
    public async Task InitializeAsync_WithoutCredentials_DoesNotSendAndLogsOnce()
    {
        int sendCount = 0;
        TelemetryHandler.TestSendAsyncOverride = _ =>
        {
            sendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        };

        int warningCountBefore = Logger
            .GetLogs()
            .Count(log => log.Content.Contains("OpenSearch credentials are not configured"));

        await TelemetryHandler.InitializeAsync();
        await TelemetryHandler.InitializeAsync();

        int warningCountAfter = Logger
            .GetLogs()
            .Count(log => log.Content.Contains("OpenSearch credentials are not configured"));

        Assert.Equal(0, sendCount);
        Assert.Equal(warningCountBefore + 1, warningCountAfter);
    }

    [Fact]
    public void ComputeActiveSettingsBitmask_IncludesDeterministicSettingsAndSpecialPaths()
    {
        Settings.Set(Settings.K.DisableAutoUpdateWingetUI, false);
        Settings.Set(Settings.K.EnableUniGetUIBeta, true);
        Settings.Set(Settings.K.DisableSystemTray, true);
        Settings.Set(Settings.K.DisableNotifications, true);
        Settings.Set(Settings.K.DisableAutoCheckforUpdates, false);
        Settings.Set(Settings.K.AutomaticallyUpdatePackages, true);
        Settings.Set(Settings.K.AskToDeleteNewDesktopShortcuts, false);
        Settings.Set(Settings.K.EnablePackageBackup_LOCAL, false);
        Settings.Set(Settings.K.DoCacheAdminRights, false);
        Settings.Set(Settings.K.DoCacheAdminRightsForBatches, false);
        Settings.Set(Settings.K.ForceLegacyBundledWinGet, false);
        Settings.Set(Settings.K.UseSystemChocolatey, false);
        File.WriteAllText(_portableMarkerPath, string.Empty);
        CoreData.WasDaemon = true;

        int activeSettings = TelemetryHandler.ComputeActiveSettingsBitmask();

        Assert.Equal(12339, activeSettings);
    }

    [Fact]
    public async Task InitializeAsync_SendsActivityPayloadWithRequiredFields()
    {
        Settings.Set(Settings.K.DisableAutoUpdateWingetUI, false);
        Settings.Set(Settings.K.EnableUniGetUIBeta, true);
        Settings.Set(Settings.K.DisableSystemTray, true);
        Settings.Set(Settings.K.DisableNotifications, true);
        Settings.Set(Settings.K.DisableAutoCheckforUpdates, false);
        Settings.Set(Settings.K.AutomaticallyUpdatePackages, true);
        File.WriteAllText(_portableMarkerPath, string.Empty);
        CoreData.WasDaemon = true;

        TelemetryHandler.Configure("telemetry-user", "telemetry-pass");
        var captured = await CaptureRequestAsync(TelemetryHandler.InitializeAsync);

        using var json = JsonDocument.Parse(captured.Body);
        JsonElement root = json.RootElement;

        Assert.Contains("activity_events", captured.RequestUri.AbsoluteUri);
        Assert.Equal("Basic", captured.Authorization?.Scheme);
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("telemetry-user:telemetry-pass")),
            captured.Authorization?.Parameter
        );
        Assert.NotEmpty(root.GetProperty("eventID").GetString());
        Assert.NotEqual(default, root.GetProperty("eventDate").GetDateTime());
        Assert.Equal(KnownInstallId, root.GetProperty("installID").GetString());
        Assert.True(root.TryGetProperty("enabledManagers", out _));
        Assert.True(root.TryGetProperty("foundManagers", out _));
        Assert.Equal(12339, root.GetProperty("activeSettings").GetInt32());
        Assert.Equal("UniGetUI", root.GetProperty("application").GetProperty("name").GetString());
        Assert.Equal("NotApplicable", root.GetProperty("application").GetProperty("dataSource").GetString());
        Assert.Equal("Free", root.GetProperty("application").GetProperty("pricing").GetString());
        Assert.NotEmpty(root.GetProperty("platform").GetProperty("name").GetString());
    }

    [Fact]
    public async Task InstallPackage_SendsPackagePayloadWithResultAndReferral()
    {
        TelemetryHandler.Configure("telemetry-user", "telemetry-pass");
        var package = new Package(
            "Telemetry Package",
            "Telemetry.Package",
            "1.0.0",
            new NullSource("Telemetry Source"),
            NullPackageManager.Instance
        );

        var captured = await CaptureRequestAsync(() =>
        {
            TelemetryHandler.InstallPackage(
                package,
                TEL_OP_RESULT.SUCCESS,
                TEL_InstallReferral.FROM_BUNDLE
            );
        });

        using var json = JsonDocument.Parse(captured.Body);
        JsonElement root = json.RootElement;

        Assert.Contains("package_events", captured.RequestUri.AbsoluteUri);
        Assert.Equal("install", root.GetProperty("operation").GetString());
        Assert.Equal(package.Id, root.GetProperty("packageId").GetString());
        Assert.Equal(package.Manager.Name, root.GetProperty("managerName").GetString());
        Assert.Equal(package.Source.Name, root.GetProperty("sourceName").GetString());
        Assert.Equal(TEL_OP_RESULT.SUCCESS.ToString(), root.GetProperty("operationResult").GetString());
        Assert.Equal(
            TEL_InstallReferral.FROM_BUNDLE.ToString(),
            root.GetProperty("eventSource").GetString()
        );
        Assert.Equal(KnownInstallId, root.GetProperty("installID").GetString());
    }

    [Fact]
    public async Task PackageDetails_SendsEventSourceWithoutOperationResult()
    {
        TelemetryHandler.Configure("telemetry-user", "telemetry-pass");
        var package = new Package(
            "Telemetry Package",
            "Telemetry.Package",
            "1.0.0",
            new NullSource("Telemetry Source"),
            NullPackageManager.Instance
        );

        var captured = await CaptureRequestAsync(() =>
        {
            TelemetryHandler.PackageDetails(package, "DIRECT_LINK");
        });

        using var json = JsonDocument.Parse(captured.Body);
        JsonElement root = json.RootElement;

        Assert.Equal("details", root.GetProperty("operation").GetString());
        Assert.Equal("DIRECT_LINK", root.GetProperty("eventSource").GetString());
        Assert.False(root.TryGetProperty("operationResult", out _));
    }

    [Fact]
    public async Task BundleOperations_SendExpectedRoutingPayloads()
    {
        TelemetryHandler.Configure("telemetry-user", "telemetry-pass");
        var captured = await CaptureRequestsAsync(
            expectedCount: 3,
            trigger: () =>
            {
                TelemetryHandler.ImportBundle(BundleFormatType.JSON);
                TelemetryHandler.ExportBundle(BundleFormatType.UBUNDLE);
                TelemetryHandler.ExportBatch();
            }
        );

        var actualRoutes = captured
            .Select(request =>
            {
                using JsonDocument json = JsonDocument.Parse(request.Body);
                JsonElement root = json.RootElement;
                return (
                    Operation: root.GetProperty("operation").GetString(),
                    BundleType: root.GetProperty("bundleType").GetString()
                );
            })
            .OrderBy(route => route.Operation, StringComparer.Ordinal)
            .ThenBy(route => route.BundleType, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                ("export", "PS1_SCRIPT"),
                ("export", BundleFormatType.UBUNDLE.ToString()),
                ("import", BundleFormatType.JSON.ToString()),
            ],
            actualRoutes
        );
    }

    private static async Task<CapturedRequest> CaptureRequestAsync(Func<Task> trigger)
    {
        TaskCompletionSource<CapturedRequest> completionSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TelemetryHandler.TestSendAsyncOverride = async request =>
        {
            completionSource.TrySetResult(await CapturedRequest.CreateAsync(request));
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        await trigger();
        return await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static Task<CapturedRequest> CaptureRequestAsync(Action trigger) =>
        CaptureRequestAsync(() =>
        {
            trigger();
            return Task.CompletedTask;
        });

    private static async Task<IReadOnlyList<CapturedRequest>> CaptureRequestsAsync(
        int expectedCount,
        Action trigger
    )
    {
        List<CapturedRequest> captured = [];
        Lock sync = new();
        TaskCompletionSource<IReadOnlyList<CapturedRequest>> completionSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        TelemetryHandler.TestSendAsyncOverride = async request =>
        {
            CapturedRequest capturedRequest = await CapturedRequest.CreateAsync(request);
            lock (sync)
            {
                captured.Add(capturedRequest);
                if (captured.Count == expectedCount)
                {
                    completionSource.TrySetResult(captured.ToArray());
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        trigger();
        return await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static void ClearSettingsCaches()
    {
        ClearDictionaryField("booleanSettings");
        ClearDictionaryField("valueSettings");
    }

    private static void ClearDictionaryField(string fieldName)
    {
        FieldInfo field = typeof(Settings).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)!;
        object dictionary = field.GetValue(null)!;
        dictionary.GetType().GetMethod("Clear")!.Invoke(dictionary, null);
    }

    private sealed record CapturedRequest(
        Uri RequestUri,
        string Body,
        System.Net.Http.Headers.AuthenticationHeaderValue? Authorization
    )
    {
        public static async Task<CapturedRequest> CreateAsync(HttpRequestMessage request) =>
            new(
                request.RequestUri!,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(),
                request.Headers.Authorization
            );
    }
}
