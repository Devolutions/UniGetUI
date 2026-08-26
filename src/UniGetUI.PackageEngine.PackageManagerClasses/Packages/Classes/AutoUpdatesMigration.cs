using System.Text.Json.Nodes;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools.Scheduling;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.Classes.Packages.Classes;

public static class AutoUpdatesMigration
{
    private const string OptionKey = "AutoUpdatePackage";
    private const string ManagerFilePrefix = "GlobalValues.";

    public static void RunOnce(IEnumerable<IPackageManager> managers)
    {
        if (Settings.Get(Settings.K.AutoUpdatedPackagesImported))
            return;

        try
        {
            int imported = Import(managers);

            if (imported > 0)
            {
                Logger.ImportantInfo(
                    $"{imported} package(s) marked with the per-package automatic update option were imported into the automatic updates list"
                );
                PreserveLegacyBehaviour();
            }

            Settings.Set(Settings.K.AutoUpdatedPackagesImported, true);
        }
        catch (Exception ex)
        {
            Logger.Error("Could not import the packages marked for automatic updates");
            Logger.Error(ex);
        }
    }

    private static int Import(IEnumerable<IPackageManager> managers)
    {
        string directory = CoreData.UniGetUIInstallationOptionsDirectory;
        if (!Directory.Exists(directory))
            return 0;

        var prefixes = managers
            .Select(m => (Prefix: m.Name.Replace(" ", "").Replace(".", "") + ".", Manager: m))
            .OrderByDescending(p => p.Prefix.Length)
            .ToList();

        List<string> imported = [];
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            string fileName = Path.GetFileName(path);
            if (fileName.StartsWith(ManagerFilePrefix, StringComparison.Ordinal))
                continue;

            var match = prefixes.FirstOrDefault(p =>
                fileName.StartsWith(p.Prefix, StringComparison.Ordinal)
            );
            if (match.Manager is null)
                continue;

            if (!IsMarkedForAutoUpdate(path))
                continue;

            string packageId = Path.GetFileNameWithoutExtension(fileName)[match.Prefix.Length..];
            if (packageId.Length is 0)
                continue;

            imported.Add($"{match.Manager.Name.ToLower()}\\{packageId}");
        }

        if (imported.Count > 0)
            AutoUpdatesDatabase.AddRange(imported);

        return imported.Count;
    }

    private static bool IsMarkedForAutoUpdate(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) is JsonObject options
                && options[OptionKey]?.GetValue<bool>() is true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not read the install options stored at {path}");
            Logger.Warn(ex);
            return false;
        }
    }

    private static void PreserveLegacyBehaviour()
    {
        if (MaintenanceScheduleStore.IsEnabled(MaintenanceTaskKind.InstallUpdates))
            return;

        var schedule = MaintenanceScheduleStore.Get(MaintenanceTaskKind.InstallUpdates);
        schedule.Enabled = true;
        schedule.Frequency = ScheduleFrequency.AfterEveryUpdateCheck;
        schedule.InstallTargets = ScheduleInstallTargets.MarkedPackagesOnly;
        MaintenanceScheduleStore.Set(MaintenanceTaskKind.InstallUpdates, schedule);

        Logger.ImportantInfo(
            "The \"Install available updates\" task was enabled for the marked packages only, matching how they were updated before"
        );
    }
}
