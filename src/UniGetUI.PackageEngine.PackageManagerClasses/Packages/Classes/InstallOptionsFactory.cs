using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.PackageClasses
{
    /// <summary>
    /// This class represents the options in which a package must be installed, updated or uninstalled.
    /// </summary>
    public static class InstallOptionsFactory
    {
        public static bool IsIdentityScopedOptionsFile(string fileName) =>
            StoragePath.IsIdentityScoped(fileName);

        private static class StoragePath
        {
            private const string IdentityScopedPrefix = "PackageOptions.";

            public static string Get(IPackageManager manager) =>
                "GlobalValues." + ManagerComponent(manager.Name) + ".json";

            public static string Get(IPackage package) =>
                IdentityScopedPrefix
                + ManagerComponent(package.Manager.Name)
                + "."
                + CoreTools.MakeValidFileName(package.Id)
                + "_"
                + IdentityHash(package)
                + ".json";

            public static string GetLegacy(IPackage package) =>
                ManagerComponent(package.Manager.Name) + "." + package.Id + ".json";

            private static string ManagerComponent(string name) =>
                CoreTools.MakeValidFileName(name.Replace(" ", "").Replace(".", ""));

            public static bool IsIdentityScoped(string fileName) =>
                fileName.StartsWith(IdentityScopedPrefix, StringComparison.Ordinal);

            private static string IdentityHash(IPackage package)
            {
                string identity = string.Join(
                    '\u0000',
                    package.Manager.Name,
                    package.Source.Name,
                    package.Id
                );
                byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));

                return Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
            }
        }

        private static bool TryResolveOptionsPath(string key, out string filePath)
        {
            try
            {
                return TryResolveOptionsPathCore(key, out filePath);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not resolve an options path for key {key}");
                Logger.Warn(ex);
                filePath = string.Empty;
                return false;
            }
        }

        private static bool TryResolveOptionsPathCore(string key, out string filePath)
        {
            filePath = string.Empty;

            string directory = Path.GetFullPath(CoreData.UniGetUIInstallationOptionsDirectory);
            string candidate = Path.GetFullPath(Path.Join(directory, key));

            if (
                !string.Equals(
                    Path.GetDirectoryName(candidate),
                    directory.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar
                    ),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal
                )
            )
                return false;

            if (!key.Equals(Path.GetFileName(candidate), StringComparison.Ordinal))
                return false;

            filePath = candidate;
            return true;
        }

        // Loading from disk (package and manager)
        public static InstallOptions LoadForPackage(IPackage package) =>
            _loadFromDisk(StoragePath.Get(package), StoragePath.GetLegacy(package));

        public static Task<InstallOptions> LoadForPackageAsync(IPackage package) =>
            Task.Run(() => LoadForPackage(package));

        public static InstallOptions LoadForManager(IPackageManager manager) =>
            _loadFromDisk(StoragePath.Get(manager));

        public static Task<InstallOptions> LoadForManagerAsync(IPackageManager manager) =>
            Task.Run(() => LoadForManager(manager));

        // Saving to disk (package and manager)
        public static void SaveForPackage(InstallOptions options, IPackage package) =>
            _saveToDisk(options, StoragePath.Get(package));

        public static Task SaveForPackageAsync(InstallOptions options, IPackage package) =>
            Task.Run(() => _saveToDisk(options, StoragePath.Get(package)));

        public static void SaveForManager(InstallOptions options, IPackageManager manager) =>
            _saveToDisk(options, StoragePath.Get(manager));

        public static Task SaveForManagerAsync(InstallOptions options, IPackageManager manager) =>
            Task.Run(() => _saveToDisk(options, StoragePath.Get(manager)));

        /// <summary>
        /// Loads the applicable InstallationOptions, and applies
        /// any required transformations in case that generic options are being used
        /// </summary>
        /// <param name="package">The package whose options to load</param>
        /// <param name="elevated">Overrides the RunAsAdmin property</param>
        /// <param name="interactive">Overrides the Interactive property</param>
        /// <param name="no_integrity">Overrides the SkipHashCheck property</param>
        /// <param name="remove_data">Overrides the RemoveDataOnUninstall property</param>
        /// <param name="overridePackageOptions">In case of on-the-fly command generation, the PACKAGE
        /// options can be overriden with this object </param>
        /// <returns>The applicable InstallOptions</returns>
        public static InstallOptions LoadApplicable(
            IPackage package,
            bool? elevated = null,
            bool? interactive = null,
            bool? no_integrity = null,
            bool? remove_data = null,
            InstallOptions? overridePackageOptions = null
        )
        {
            var instance = overridePackageOptions ?? LoadForPackage(package);

            // A location typed into this package's own options is an explicit, per-package choice;
            // one inherited from the manager default below is not. Consumers (e.g. WinGet updates)
            // use this to honor explicit locations while keeping manager-wide defaults opt-in.
            bool locationIsExplicit = instance.OverridesNextLevelOpts;

            if (!instance.OverridesNextLevelOpts)
            {
                Logger.Debug(
                    $"Package {package.Id} does not override options, will use package manager's default..."
                );
                instance = LoadForManager(package.Manager);

                var legalizedId = CoreTools.MakeValidFileName(package.Id);
                instance.CustomInstallLocation = instance.CustomInstallLocation.Replace(
                    "%PACKAGE%",
                    legalizedId
                );
            }

            instance.CustomInstallLocationIsExplicit = locationIsExplicit;

            if (elevated is not null)
                instance.RunAsAdministrator = (bool)elevated;
            if (interactive is not null)
                instance.InteractiveInstallation = (bool)interactive;
            if (no_integrity is not null)
                instance.SkipHashCheck = (bool)no_integrity;
            if (remove_data is not null)
                instance.RemoveDataOnUninstall = (bool)remove_data;

            return EnsureSecureOptions(instance);
        }

        /// <summary>
        /// Loads the applicable InstallationOptions, and applies
        /// any required transformations in case that generic options are being used
        /// </summary>
        /// <param name="package">The package whose options to load</param>
        /// <param name="elevated">Overrides the RunAsAdmin property</param>
        /// <param name="interactive">Overrides the Interactive property</param>
        /// <param name="no_integrity">Overrides the SkipHashCheck property</param>
        /// <param name="remove_data">Overrides the RemoveDataOnUninstall property</param>
        /// <param name="overridePackageOptions">In case of on-the-fly command generation, the PACKAGE
        /// options can be overriden with this object </param>
        /// <returns>The applicable InstallOptions</returns>
        public static Task<InstallOptions> LoadApplicableAsync(
            IPackage package,
            bool? elevated = null,
            bool? interactive = null,
            bool? no_integrity = null,
            bool? remove_data = null,
            InstallOptions? overridePackageOptions = null
        ) =>
            Task.Run(() =>
                LoadApplicable(
                    package,
                    elevated,
                    interactive,
                    no_integrity,
                    remove_data,
                    overridePackageOptions
                )
            );

        /*
         *
         * SAVE TO DISK MECHANISMS
         *
         */

        private static readonly ConcurrentDictionary<string, InstallOptions> _optionsCache = new();

        private static void _saveToDisk(InstallOptions options, string key)
        {
            try
            {
                if (!TryResolveOptionsPath(key, out string filePath))
                {
                    Logger.Error($"Refused to save options to an unsafe path for key {key}");
                    return;
                }

                _optionsCache[key] = options.Copy();

                string fileContents = options.AsJsonString();
                File.WriteAllText(filePath, fileContents);
            }
            catch (Exception ex)
            {
                Logger.Error($"Could not save {key} options to disk");
                Logger.Error(ex);
            }
        }

        private static InstallOptions _loadFromDisk(string key, string? legacyKey = null)
        {
            if (
                legacyKey is not null
                && !_optionsCache.ContainsKey(key)
                && TryResolveOptionsPath(key, out string preferred)
                && !File.Exists(preferred)
                && TryResolveOptionsPath(legacyKey, out string legacy)
                && File.Exists(legacy)
            )
            {
                key = legacyKey;
            }

            if (!TryResolveOptionsPath(key, out string filePath))
            {
                Logger.Error($"Refused to load options from an unsafe path for key {key}");
                return new InstallOptions();
            }

            try
            {
                InstallOptions serializedOptions;
                if (_optionsCache.TryGetValue(key, out var cached))
                {
                    // If the wanted instance is already cached
                    return cached.Copy();
                }
                else
                {
                    if (!File.Exists(filePath))
                    {
                        // If the file where it should be stored does not exist
                        _optionsCache[key] = new InstallOptions();
                        return new InstallOptions();
                    }
                    else
                    {
                        // If the options are not cached, and the save file exists
                        var rawData = File.ReadAllText(filePath);
                        JsonNode? jsonData = JsonNode.Parse(rawData);
                        ArgumentNullException.ThrowIfNull(jsonData);
                        serializedOptions = new InstallOptions(jsonData);
                        _optionsCache[key] = serializedOptions;
                        return serializedOptions.Copy();
                    }
                }
            }
            catch (JsonException)
            {
                Logger.Warn(
                    "An error occurred while parsing package "
                        + key
                        + ". The file will be overwritten"
                );
                try
                {
                    File.WriteAllText(filePath, "{}");
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex);
                }
                return new();
            }
            catch (Exception e)
            {
                Logger.Error("Loading installation options for file " + key + " have failed: ");
                Logger.Error(e);
                return new();
            }
        }

        private static InstallOptions EnsureSecureOptions(InstallOptions options)
        {
            options.CustomInstallLocation = _expandEnvironmentVariables(options.CustomInstallLocation);

            if (SecureSettings.Get(SecureSettings.K.AllowCLIArguments))
            {
                _expandAndSanitizeCliArguments(options.CustomParameters_Install);
                _expandAndSanitizeCliArguments(options.CustomParameters_Update);
                _expandAndSanitizeCliArguments(options.CustomParameters_Uninstall);
            }
            else
            {
                // Otherwhise, clear them
                if (options.CustomParameters_Install.Count > 0)
                    Logger.Warn(
                        $"Custom install parameters [{string.Join(' ', options.CustomParameters_Install)}] will be discarded"
                    );
                if (options.CustomParameters_Update.Count > 0)
                    Logger.Warn(
                        $"Custom update parameters [{string.Join(' ', options.CustomParameters_Update)}] will be discarded"
                    );
                if (options.CustomParameters_Uninstall.Count > 0)
                    Logger.Warn(
                        $"Custom uninstall parameters [{string.Join(' ', options.CustomParameters_Uninstall)}] will be discarded"
                    );

                options.CustomParameters_Install = [];
                options.CustomParameters_Update = [];
                options.CustomParameters_Uninstall = [];
            }

            if (!SecureSettings.Get(SecureSettings.K.AllowPrePostOpCommand))
            {
                if (options.PreInstallCommand.Any())
                    Logger.Warn(
                        $"Pre-install command {options.PreInstallCommand} will be discarded"
                    );
                if (options.PostInstallCommand.Any())
                    Logger.Warn(
                        $"Post-install command {options.PostInstallCommand} will be discarded"
                    );
                if (options.PreUpdateCommand.Any())
                    Logger.Warn($"Pre-update command {options.PreUpdateCommand} will be discarded");
                if (options.PostUpdateCommand.Any())
                    Logger.Warn(
                        $"Post-update command {options.PostUpdateCommand} will be discarded"
                    );
                if (options.PreUninstallCommand.Any())
                    Logger.Warn(
                        $"Pre-uninstall command {options.PreUninstallCommand} will be discarded"
                    );
                if (options.PostUninstallCommand.Any())
                    Logger.Warn(
                        $"Post-uninstall command {options.PostUninstallCommand} will be discarded"
                    );

                options.PreInstallCommand = "";
                options.PostInstallCommand = "";
                options.PreUpdateCommand = "";
                options.PostUpdateCommand = "";
                options.PreUninstallCommand = "";
                options.PostUninstallCommand = "";
            }

            return options;
        }

        private static void _expandAndSanitizeCliArguments(List<string> parameters)
        {
            for (int i = 0; i < parameters.Count; i++)
            {
                parameters[i] = _expandEnvironmentVariables(parameters[i])
                    .Replace("&", "")
                    .Replace("|", "")
                    .Replace(";", "")
                    .Replace("<", "")
                    .Replace(">", "")
                    .Replace("\n", "");
            }
        }

        private static string _expandEnvironmentVariables(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            return Settings.Get(Settings.K.ExpandEnvVarsWithPercentSyntax)
                ? _expandPercentVariables(value)
                : _expandAngleBracketVariables(value);
        }

        private static string _expandPercentVariables(string value)
        {
            if (!value.Contains('%'))
                return value;

            try
            {
                return Environment.ExpandEnvironmentVariables(value);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not expand environment variables in \"{value}\"");
                Logger.Warn(ex);
                return value;
            }
        }

        private static string _expandAngleBracketVariables(string value)
        {
            if (!value.Contains('<'))
                return value;

            StringBuilder result = new();
            int i = 0;
            while (i < value.Length)
            {
                if (value[i] == '<')
                {
                    int end = value.IndexOf('>', i + 1);
                    if (end > i + 1)
                    {
                        string name = value.Substring(i + 1, end - i - 1);
                        string? resolved = null;
                        try
                        {
                            resolved = Environment.GetEnvironmentVariable(name);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"Could not read environment variable \"{name}\"");
                            Logger.Warn(ex);
                        }

                        if (resolved is not null)
                        {
                            result.Append(resolved);
                            i = end + 1;
                            continue;
                        }
                    }
                }

                result.Append(value[i]);
                i++;
            }
            return result.ToString();
        }
    }
}
