using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.Tui.Infrastructure;

namespace UniGetUI.Tui.Views.Dialogs;

/// <summary>
/// "Manage ignored updates", ported from <c>ManageIgnoredUpdatesViewModel</c>: lists the ignored-updates
/// database, lets the user stop ignoring one entry (which puts a known update back on the Updates page)
/// or reset the whole list.
/// </summary>
internal static class IgnoredUpdatesDialog
{
    public sealed record Entry(string IgnoredId, string PackageId, string Name, string Manager, string Version, string NewVersion);

    public static IReadOnlyList<Entry> LoadEntries()
    {
        var managerMap = TuiEngine.Managers.ToDictionary(m => m.Properties.Name.ToLowerInvariant(), m => m);
        var list = new List<Entry>();
        foreach (var (ignoredId, version) in IgnoredUpdatesDatabase.GetDatabase().OrderBy(x => x.Key))
        {
            string[] parts = ignoredId.Split('\\');
            string managerKey = parts[0];
            string packageId = parts.Length > 1 ? parts[^1] : ignoredId;
            string managerDisplay = managerMap.TryGetValue(managerKey, out var mgr) ? mgr.DisplayName : managerKey;
            string versionDisplay = version == "*" ? CoreTools.Translate("All versions")
                : version.StartsWith('<') ? CoreTools.Translate("Paused until {0}", version[1..]) : version;
            string current = InstalledPackagesLoader.Instance.GetPackageForId(packageId)?.VersionString ?? CoreTools.Translate("Unknown");
            string newVersion;
            if (UpgradablePackagesLoader.Instance.IgnoredPackages.TryGetValue(packageId, out var upgradable)
                && upgradable.NewVersionString != upgradable.VersionString)
                newVersion = current + " -> " + upgradable.NewVersionString;
            else if (current != CoreTools.Translate("Unknown"))
                newVersion = CoreTools.Translate("Up to date") + $" ({current})";
            else
                newVersion = CoreTools.Translate("Unknown");
            list.Add(new Entry(ignoredId, packageId, CoreTools.FormatAsName(packageId), managerDisplay, versionDisplay, newVersion));
        }

        return list;
    }

    public static async Task RemoveAsync(Entry entry)
    {
        await Task.Run(() => IgnoredUpdatesDatabase.Remove(entry.IgnoredId));
        if (UpgradablePackagesLoader.Instance.IgnoredPackages.TryRemove(entry.PackageId, out var pkg)
            && pkg.NewVersionString != pkg.VersionString)
            await UpgradablePackagesLoader.Instance.AddForeign(pkg);

        foreach (var installed in InstalledPackagesLoader.Instance.Packages)
        {
            if (installed.Id == entry.PackageId)
            {
                installed.SetTag(PackageTag.Default);
                break;
            }
        }
    }

    public static async Task ShowAsync()
    {
        while (true)
        {
            var entries = LoadEntries();
            var choices = entries
                .Select(e => new TuiChoice($"{e.Name,-28} {e.Manager,-12} {e.Version,-22} {e.NewVersion}"))
                .ToList();
            if (choices.Count == 0) choices.Add(new TuiChoice(CoreTools.Translate("No ignored updates"), Enabled: false));
            choices.Add(TuiChoice.Separator);
            choices.Add(new TuiChoice(CoreTools.Translate("Reset list"), Enabled: entries.Count > 0));

            int? picked = await TuiPrompts.ChooseAsync(CoreTools.Translate("Manage ignored updates"), choices, 0,
                CoreTools.Translate("The packages listed here won't be taken in account when checking for updates.")
                + " " + CoreTools.Translate("Press Enter on a package to stop ignoring its updates."));
            if (picked is not int index) return;

            if (index < entries.Count)
            {
                await RemoveAsync(entries[index]);
                TuiNotifications.Success(CoreTools.Translate("Ignored updates"),
                    CoreTools.Translate("Updates for {0} will no longer be ignored", entries[index].Name));
                continue;
            }

            if (index == choices.Count - 1
                && await TuiPrompts.ConfirmAsync(CoreTools.Translate("Reset list"),
                    CoreTools.Translate("Do you really want to reset the ignored updates list? This action cannot be reverted")))
            {
                foreach (var entry in entries) await RemoveAsync(entry);
            }
        }
    }
}
