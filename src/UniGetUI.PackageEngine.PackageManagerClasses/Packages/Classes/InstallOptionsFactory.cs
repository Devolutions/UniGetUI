using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
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
        private static class StoragePath
        {
            public static string Get(IPackageManager manager) =>
                "GlobalValues." + manager.Name.Replace(" ", "").Replace(".", "") + ".json";

            public static string Get(IPackage package) =>
                package.Manager.Name.Replace(" ", "").Replace(".", "") + "." + package.Id + ".json";
        }

        // Loading from disk (package and manager)
        public static InstallOptions LoadForPackage(IPackage package) =>
            _loadFromDisk(StoragePath.Get(package));

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
                var filePath = Path.Join(CoreData.UniGetUIInstallationOptionsDirectory, key);
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

        private static InstallOptions _loadFromDisk(string key)
        {
            var filePath = Path.Join(CoreData.UniGetUIInstallationOptionsDirectory, key);
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
            if (SecureSettings.Get(SecureSettings.K.AllowCLIArguments))
            {
                // If CLI arguments are allowed, sanitize them
                for (int i = 0; i < options.CustomParameters_Install.Count; i++)
                {
                    options.CustomParameters_Install[i] = options
                        .CustomParameters_Install[i]
                        .Replace("&", "")
                        .Replace("|", "")
                        .Replace(";", "")
                        .Replace("<", "")
                        .Replace(">", "")
                        .Replace("\n", "");
                }
                for (int i = 0; i < options.CustomParameters_Update.Count; i++)
                {
                    options.CustomParameters_Update[i] = options
                        .CustomParameters_Update[i]
                        .Replace("&", "")
                        .Replace("|", "")
                        .Replace(";", "")
                        .Replace("<", "")
                        .Replace(">", "")
                        .Replace("\n", "");
                }
                for (int i = 0; i < options.CustomParameters_Uninstall.Count; i++)
                {
                    options.CustomParameters_Uninstall[i] = options
                        .CustomParameters_Uninstall[i]
                        .Replace("&", "")
                        .Replace("|", "")
                        .Replace(";", "")
                        .Replace("<", "")
                        .Replace(">", "")
                        .Replace("\n", "");
                }
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
    }
}
