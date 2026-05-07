using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.PackageClasses;
using WindowsPackageManager.Interop;
using Architecture = UniGetUI.PackageEngine.Enums.Architecture;

namespace UniGetUI.PackageEngine.Managers.WingetManager
{
    public class WinGet : PackageManager
    {
        internal const string CliToolPreferenceEnvironmentVariable = "UNIGETUI_WINGET_CLI";
        internal const string ComApiPolicyEnvironmentVariable = "UNIGETUI_WINGET_COM";
        private const string SystemWinGetExecutableName = "winget.exe";
        private const string PingetExecutableName = "pinget.exe";

        public static string[] FALSE_PACKAGE_NAMES = ["", "e(s)", "have", "the", "Id"];
        public static string[] FALSE_PACKAGE_IDS =
        [
            "",
            "e(s)",
            "have",
            "an",
            "'winget",
            "pin'",
            "have",
            "an",
            "Version",
        ];
        public static string[] FALSE_PACKAGE_VERSIONS =
        [
            "",
            "have",
            "an",
            "'winget",
            "pin'",
            "have",
            "an",
            "Version",
        ];
        public LocalWinGetSource LocalPcSource { get; }
        public LocalWinGetSource AndroidSubsystemSource { get; }
        public LocalWinGetSource SteamSource { get; }
        public LocalWinGetSource UbisoftConnectSource { get; }
        public LocalWinGetSource GOGSource { get; }
        public LocalWinGetSource MicrosoftStoreSource { get; }
        public static bool NO_PACKAGES_HAVE_BEEN_LOADED { get; private set; }
        internal WinGetCliToolKind SelectedCliToolKind { get; private set; } =
            WinGetCliToolKind.SystemWinGet;

        public WinGet()
        {
            Capabilities = new ManagerCapabilities
            {
                CanRunAsAdmin = true,
                CanSkipIntegrityChecks = true,
                CanRunInteractively = true,
                SupportsCustomVersions = true,
                CanDownloadInstaller = true,
                CanListDependencies = true,
                SupportsCustomArchitectures = true,
                SupportedCustomArchitectures =
                [
                    Architecture.x86,
                    Architecture.x64,
                    Architecture.arm64,
                ],
                SupportsCustomScopes = true,
                SupportsCustomLocations = true,
                SupportsCustomSources = true,
                SupportsCustomPackageIcons = true,
                SupportsCustomPackageScreenshots = true,
                Sources = new SourceCapabilities
                {
                    KnowsPackageCount = false,
                    KnowsUpdateDate = true,
                    MustBeInstalledAsAdmin = true,
                },
                SupportsProxy = ProxySupport.Partially,
                SupportsProxyAuth = false,
            };

            Properties = new ManagerProperties
            {
                Id = "winget",
                Name = "Winget",
                DisplayName = "WinGet",
                Description = CoreTools.Translate(
                    "Microsoft's official package manager. Full of well-known and verified packages<br>Contains: <b>General Software, Microsoft Store apps</b>"
                ),
                IconId = IconType.WinGet,
                ColorIconId = "winget_color",
                ExecutableFriendlyName = "winget.exe",
                InstallVerb = "install",
                UninstallVerb = "uninstall",
                UpdateVerb = "update",
                KnownSources =
                [
                    new ManagerSource(
                        this,
                        "winget",
                        new Uri("https://cdn.winget.microsoft.com/cache")
                    ),
                    new ManagerSource(
                        this,
                        "winget-fonts",
                        new Uri("https://cdn.winget.microsoft.com/fonts")
                    ),
                    new ManagerSource(
                        this,
                        "msstore",
                        new Uri("https://storeedgefd.dsx.mp.microsoft.com/v9.0")
                    ),
                ],
                DefaultSource = new ManagerSource(
                    this,
                    "winget",
                    new Uri("https://cdn.winget.microsoft.com/cache")
                ),
            };

            SourcesHelper = new WinGetSourceHelper(this);
            DetailsHelper = new WinGetPkgDetailsHelper(this);
            OperationHelper = new WinGetPkgOperationHelper(this);

            LocalPcSource = new LocalWinGetSource(
                this,
                CoreTools.Translate("Local PC"),
                IconType.LocalPc,
                LocalWinGetSource.Type_t.LocalPC
            );
            AndroidSubsystemSource = new(
                this,
                CoreTools.Translate("Android Subsystem"),
                IconType.Android,
                LocalWinGetSource.Type_t.Android
            );
            SteamSource = new(this, "Steam", IconType.Steam, LocalWinGetSource.Type_t.Steam);
            UbisoftConnectSource = new(
                this,
                "Ubisoft Connect",
                IconType.UPlay,
                LocalWinGetSource.Type_t.Ubisoft
            );
            GOGSource = new(this, "GOG", IconType.GOG, LocalWinGetSource.Type_t.GOG);
            MicrosoftStoreSource = new(
                this,
                "Microsoft Store",
                IconType.MsStore,
                LocalWinGetSource.Type_t.MicrosftStore
            );
        }

        public static string GetProxyArgument()
        {
            if (!Settings.Get(Settings.K.EnableProxy))
                return "";
            var proxyUri = Settings.GetProxyUrl();
            if (proxyUri is null)
                return "";

            if (Settings.Get(Settings.K.EnableProxyAuth))
            {
                Logger.Warn(
                    "Proxy is enabled, but WinGet does not support proxy authentication, so the proxy setting will be ignored"
                );
                return "";
            }
            return $"--proxy {proxyUri.ToString().TrimEnd('/')}";
        }

        /// <summary>
        /// Returns the set of installer URL hosts from the WinGet manifest for a specific
        /// version of the given package, or null if it can't be resolved. Used for the
        /// installer-host-change warning on the Updates page (issue #4617).
        /// Returns a set (not a single host) so callers can do set-overlap comparison —
        /// see PingetPackageDetailsProvider.TryGetInstallerHostsForVersion for rationale.
        /// </summary>
        public static IReadOnlySet<string>? TryGetInstallerHostsForVersion(
            UniGetUI.PackageEngine.Interfaces.IPackage package,
            string version
        )
        {
            return PingetPackageDetailsProvider.TryGetInstallerHostsForVersion(package, version);
        }

        protected override IReadOnlyList<Package> FindPackages_UnSafe(string query)
        {
            return WinGetHelper.Instance.FindPackages_UnSafe(query);
        }

        protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
        {
            return WinGetHelper
                .Instance.GetAvailableUpdates_UnSafe()
                .Where(p => p.Id != "Chocolatey.Chocolatey")
                .ToArray();
        }

        protected override IReadOnlyList<Package> GetInstalledPackages_UnSafe()
        {
            try
            {
                var packages = WinGetHelper.Instance.GetInstalledPackages_UnSafe();
                NO_PACKAGES_HAVE_BEEN_LOADED = false;
                return packages;
            }
            catch (Exception)
            {
                NO_PACKAGES_HAVE_BEEN_LOADED = true;
                throw;
            }
        }

        public ManagerSource GetLocalSource(string id)
        {
            var IdPieces = id.Split('\\');
            if (IdPieces[0] == "MSIX")
            {
                return MicrosoftStoreSource;
            }

            string MeaningfulId = IdPieces[^1];

            // Fast Local PC Check
            if (MeaningfulId[0] == '{')
            {
                return LocalPcSource;
            }

            // Check if source is android
            if (
                MeaningfulId.Count(x => x == '.') >= 2
                && MeaningfulId.All(c =>
                    (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '…'
                )
            )
            {
                return AndroidSubsystemSource;
            }

            // Check if source is Steam
            if (MeaningfulId == "Steam" || MeaningfulId.StartsWith("Steam App"))
            {
                return SteamSource;
            }

            // Check if source is Ubisoft Connect
            if (MeaningfulId == "Uplay" || MeaningfulId.StartsWith("Uplay Install"))
            {
                return UbisoftConnectSource;
            }

            // Check if source is GOG
            if (
                MeaningfulId.EndsWith("_is1")
                && MeaningfulId.Replace("_is1", "").All(c => (c >= '0' && c <= '9'))
            )
            {
                return GOGSource;
            }

            // Otherwise they are Local PC
            return LocalPcSource;
        }

        public override IReadOnlyList<string> FindCandidateExecutableFiles()
        {
            return FindCandidateExecutableFiles(
                executableName => CoreTools.WhichMultiple(executableName),
                File.Exists,
                GetBundledPingetExecutablePath(),
                GetCliToolPreference()
            );
        }

        internal static IReadOnlyList<string> FindCandidateExecutableFiles(
            Func<string, IReadOnlyList<string>> findExecutables,
            Func<string, bool> fileExists,
            string bundledPingetPath,
            WinGetCliToolPreference cliToolPreference = WinGetCliToolPreference.Default
        )
        {
            List<string> candidates = [];

            if (cliToolPreference is not WinGetCliToolPreference.BundledPinget)
            {
                candidates.AddRange(findExecutables(SystemWinGetExecutableName));
            }

            if (cliToolPreference is not WinGetCliToolPreference.SystemWinGet)
            {
                candidates.AddRange(
                    FindPingetExecutableFiles(findExecutables, fileExists, bundledPingetPath)
                );
            }

            return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static IEnumerable<string> FindPingetExecutableFiles(
            Func<string, IReadOnlyList<string>> findExecutables,
            Func<string, bool> fileExists,
            string bundledPingetPath
        )
        {
            if (fileExists(bundledPingetPath))
            {
                yield return bundledPingetPath;
            }

            foreach (string pingetExecutablePath in findExecutables(PingetExecutableName))
            {
                yield return pingetExecutablePath;
            }
        }

        internal static string GetBundledPingetExecutablePath()
        {
            return GetBundledPingetExecutablePath(CoreData.UniGetUIExecutableDirectory, File.Exists);
        }

        internal static string GetBundledPingetExecutablePath(
            string executableDirectory,
            Func<string, bool> fileExists
        )
        {
            string rootPingetPath = Path.Join(executableDirectory, PingetExecutableName);
            if (fileExists(rootPingetPath))
            {
                return rootPingetPath;
            }

            string avaloniaPingetPath = Path.Join(
                executableDirectory,
                "Avalonia",
                PingetExecutableName
            );
            return fileExists(avaloniaPingetPath) ? avaloniaPingetPath : rootPingetPath;
        }

        internal IWinGetManagerHelper CreateCliHelperForSelectedCliTool()
        {
            return SelectedCliToolKind == WinGetCliToolKind.BundledPinget
                ? new PingetCliHelper(this, Status.ExecutablePath)
                : new WinGetCliHelper(this, Status.ExecutablePath);
        }

        protected override void _loadManagerExecutableFile(
            out bool found,
            out string path,
            out string callArguments
        )
        {
            var (_found, _path) = GetExecutableFile();
            found = _found;
            path = _path;
            callArguments = "";

            if (!found)
            {
                return;
            }

            SelectedCliToolKind = GetCliToolKind(path);

            if (SelectedCliToolKind == WinGetCliToolKind.BundledPinget)
            {
                Logger.Warn("Using Pinget CLI tool.");
                WinGetHelper.Instance = new PingetCliHelper(this, path);
                return;
            }

            WinGetComApiPolicy comApiPolicy = GetComApiPolicy();
            if (!ShouldUseWinGetComApi(SelectedCliToolKind, comApiPolicy))
            {
                Logger.Warn("WinGet COM API usage is disabled; using WinGetCliHelper().");
                WinGetHelper.Instance = new WinGetCliHelper(this, path);
                return;
            }

            try
            {
                WinGetHelper.Instance = new NativeWinGetHelper(this);
            }
            catch (Exception ex)
            {
                if (
                    ex is WinGetComActivationException activationEx
                    && activationEx.IsExpectedFallbackCondition
                )
                {
                    Logger.Warn(
                        $"Native WinGet helper is unavailable on this machine ({activationEx.HResultHex}: {activationEx.Reason})"
                    );
                }
                else
                {
                    Logger.Warn(
                        $"Cannot instantiate Native WinGet Helper due to error: {ex.Message}"
                    );
                    Logger.Warn(ex);
                }

                Logger.Warn("WinGet will resort to using WinGetCliHelper()");
                WinGetHelper.Instance = CreateCliHelperForSelectedCliTool();
            }
        }

        internal static WinGetCliToolPreference GetCliToolPreference()
        {
            return GetCliToolPreference(
                static name => Environment.GetEnvironmentVariable(name),
                static key => Settings.GetValue(key)
            );
        }

        internal static WinGetCliToolPreference GetCliToolPreference(
            Func<string, string?> getEnvironmentVariable,
            Func<Settings.K, string> getSettingValue
        )
        {
            string? value = GetPolicyValue(
                CliToolPreferenceEnvironmentVariable,
                Settings.K.WinGetCliToolPreference,
                getEnvironmentVariable,
                getSettingValue
            );

            return ParseCliToolPreference(value) ?? WinGetCliToolPreference.Default;
        }

        internal static WinGetComApiPolicy GetComApiPolicy()
        {
            return GetComApiPolicy(
                static name => Environment.GetEnvironmentVariable(name),
                static key => Settings.GetValue(key)
            );
        }

        internal static WinGetComApiPolicy GetComApiPolicy(
            Func<string, string?> getEnvironmentVariable,
            Func<Settings.K, string> getSettingValue
        )
        {
            string? value = GetPolicyValue(
                ComApiPolicyEnvironmentVariable,
                Settings.K.WinGetComApiPolicy,
                getEnvironmentVariable,
                getSettingValue
            );

            return ParseComApiPolicy(value) ?? WinGetComApiPolicy.Default;
        }

        private static string? GetPolicyValue(
            string environmentVariableName,
            Settings.K settingKey,
            Func<string, string?> getEnvironmentVariable,
            Func<Settings.K, string> getSettingValue
        )
        {
            string? environmentValue = getEnvironmentVariable(environmentVariableName);
            if (!string.IsNullOrWhiteSpace(environmentValue))
            {
                return environmentValue;
            }

            string settingValue = getSettingValue(settingKey);
            return string.IsNullOrWhiteSpace(settingValue) ? null : settingValue;
        }

        private static WinGetCliToolPreference? ParseCliToolPreference(string? value)
        {
            return NormalizeCliToolPreferenceValue(value) switch
            {
                "default" => WinGetCliToolPreference.Default,
                "winget" => WinGetCliToolPreference.SystemWinGet,
                "pinget" => WinGetCliToolPreference.BundledPinget,
                _ => null,
            };
        }

        private static string NormalizeCliToolPreferenceValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToLowerInvariant();
        }

        private static WinGetComApiPolicy? ParseComApiPolicy(string? value)
        {
            return NormalizePolicyValue(value) switch
            {
                "default" => WinGetComApiPolicy.Default,
                "enabled" or "enable" or "on" or "true" or "1" => WinGetComApiPolicy.Enabled,
                "disabled" or "disable" or "off" or "false" or "0" => WinGetComApiPolicy.Disabled,
                _ => null,
            };
        }

        internal static bool ShouldUseWinGetComApi(
            WinGetCliToolKind cliToolKind,
            WinGetComApiPolicy comApiPolicy
        )
        {
            return cliToolKind == WinGetCliToolKind.SystemWinGet
                && comApiPolicy != WinGetComApiPolicy.Disabled;
        }

        private static string NormalizePolicyValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? ""
                : value.Trim().Replace("-", "").Replace("_", "").ToLowerInvariant();
        }

        internal static WinGetCliToolKind GetCliToolKind(string executablePath)
        {
            return IsPingetExecutablePath(executablePath)
                ? WinGetCliToolKind.BundledPinget
                : WinGetCliToolKind.SystemWinGet;
        }

        private static bool IsPingetExecutablePath(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return false;
            }

            return Path.GetFileName(executablePath).Equals(
                    PingetExecutableName,
                    StringComparison.OrdinalIgnoreCase
                )
                || Path.GetFullPath(executablePath)
                    .Equals(
                        Path.GetFullPath(GetBundledPingetExecutablePath()),
                        StringComparison.OrdinalIgnoreCase
                    );
        }

        protected override void _loadManagerVersion(out string version)
        {
            bool usesCliHelper = WinGetHelper.Instance is WinGetCliHelper;
            bool usesPingetHelper = WinGetHelper.Instance is PingetCliHelper;

            Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Status.ExecutablePath,
                    Arguments = Status.ExecutableCallArgs + " --version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                },
            };

            if (CoreTools.IsAdministrator())
            {
                string WinGetTemp = Path.Join(Path.GetTempPath(), "UniGetUI", "ElevatedWinGetTemp");
                process.StartInfo.Environment["TEMP"] = WinGetTemp;
                process.StartInfo.Environment["TMP"] = WinGetTemp;
            }
            process.Start();

            string rawVersion = process.StandardOutput.ReadToEnd().Trim();
            version = usesPingetHelper
                ? $"Pinget CLI Version: {rawVersion}"
                : $"System WinGet (CLI) Version: {rawVersion}";

            if (usesPingetHelper)
                version += "\nUsing Pinget CLI helper (JSON parsing)";
            else if (usesCliHelper)
                version += "\nUsing WinGet CLI helper (CLI parsing)";
            else
            {
                version += "\nUsing Native WinGet helper (COM Api)";

                if (WinGetHelper.Instance is NativeWinGetHelper nativeHelper)
                {
                    version += $"\nActivation mode: {nativeHelper.ActivationMode}";
                    version += $"\nActivation source: {nativeHelper.ActivationSource}";
                }
            }

            string error = process.StandardError.ReadToEnd();
            if (error != "")
                Logger.Error("WinGet STDERR not empty: " + error);
        }

        protected override void _performExtraLoadingSteps()
        {
            TryRepairTempFolderPermissions();
        }

        private void ReRegisterCOMServer()
        {
            WinGetHelper.Instance = new NativeWinGetHelper(this);
            NativePackageHandler.Clear();
        }

        public override void AttemptFastRepair()
        {
            try
            {
                TryRepairTempFolderPermissions();
                if (WinGetHelper.Instance is NativeWinGetHelper)
                {
                    if (
                        WinGetHelper.Instance is NativeWinGetHelper nativeHelper
                        && nativeHelper.HasActiveLocalPackageQuery
                    )
                    {
                        Logger.Warn(
                            "WinGet local package enumeration is still running; skipping COM reconnection so the retry can attach to the in-flight task."
                        );
                        return;
                    }

                    Logger.ImportantInfo("Attempting to reconnect to WinGet COM Server...");
                    ReRegisterCOMServer();
                    NO_PACKAGES_HAVE_BEEN_LOADED = false;
                }
                else
                {
                    Logger.Warn(
                        "Attempted to reconnect to COM Server but the active backend is not native WinGet."
                    );
                }
            }
            catch (Exception ex)
            {
                Logger.Error("An error ocurred while attempting to reconnect to COM Server");
                Logger.Error(ex);
            }
        }

        private static void TryRepairTempFolderPermissions()
        {
            // if (Settings.Get(Settings.K.DisableNewWinGetTroubleshooter)) return;

            try
            {
                string tempPath = Path.GetTempPath();
                string winGetTempPath = Path.Combine(tempPath, "WinGet");

                if (!Directory.Exists(winGetTempPath))
                {
                    Logger.Warn("WinGet temp folder does not exist, creating it...");
                    Directory.CreateDirectory(winGetTempPath);
                }

                var directoryInfo = new DirectoryInfo(winGetTempPath);
                var accessControl = directoryInfo.GetAccessControl();
                var rules = accessControl.GetAccessRules(true, true, typeof(NTAccount));

                bool userHasAccess = false;
                string currentUser = WindowsIdentity.GetCurrent().Name;

                foreach (FileSystemAccessRule rule in rules)
                {
                    if (
                        rule.IdentityReference.Value.Equals(
                            currentUser,
                            StringComparison.CurrentCultureIgnoreCase
                        )
                    )
                    {
                        userHasAccess = true;
                        break;
                    }
                }

                if (!userHasAccess)
                {
                    Logger.Warn(
                        "WinGet temp folder does not have correct permissions set, adding the current user..."
                    );
                    var rule = new FileSystemAccessRule(
                        currentUser,
                        FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow
                    );

                    accessControl.AddAccessRule(rule);
                    directoryInfo.SetAccessControl(accessControl);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(
                    "An error occurred while attempting to properly configure WinGet's temp folder permissions."
                );
                Logger.Error(ex);
            }
        }

        public override void RefreshPackageIndexes()
        {
            using Process p = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Status.ExecutablePath,
                    Arguments =
                        Status.ExecutableCallArgs
                        + " source update --disable-interactivity "
                        + GetCliToolProxyArgument(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                },
            };

            IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.RefreshIndexes, p);

            if (CoreTools.IsAdministrator())
            {
                string WinGetTemp = Path.Join(Path.GetTempPath(), "UniGetUI", "ElevatedWinGetTemp");
                logger.AddToStdErr(
                    $"[WARN] Redirecting %TEMP% folder to {WinGetTemp}, since UniGetUI was run as admin"
                );
                p.StartInfo.Environment["TEMP"] = WinGetTemp;
                p.StartInfo.Environment["TMP"] = WinGetTemp;
            }

            p.Start();
            logger.AddToStdOut(p.StandardOutput.ReadToEnd());
            logger.AddToStdErr(p.StandardError.ReadToEnd());
            logger.Close(p.ExitCode);
            p.WaitForExit();
            p.Close();
        }

        private string GetCliToolProxyArgument()
        {
            return SelectedCliToolKind == WinGetCliToolKind.SystemWinGet
                ? GetProxyArgument()
                : "";
        }
    }

    public class LocalWinGetSource : ManagerSource
    {
        public enum Type_t
        {
            LocalPC,
            MicrosftStore,
            Steam,
            GOG,
            Android,
            Ubisoft,
        }

        public readonly Type_t Type;
        private readonly string name;
        private readonly IconType __icon_id;
        public override IconType IconId
        {
            get => __icon_id;
        }

        public LocalWinGetSource(WinGet manager, string name, IconType iconId, Type_t type)
            : base(
                manager,
                name,
                new Uri("https://microsoft.com/local-pc-source"),
                isVirtualManager: true
            )
        {
            Type = type;
            this.name = name;
            __icon_id = iconId;
            AsString = Name;
            AsString_DisplayName = Name;
        }
    }
}
