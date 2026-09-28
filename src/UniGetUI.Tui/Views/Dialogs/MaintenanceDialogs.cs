using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.Tui.Infrastructure;

namespace UniGetUI.Tui.Views.Dialogs;

/// <summary>
/// "Manage automatic updates" (desktop <c>ManageAutoUpdatesWindow</c>): mark installed packages for the
/// scheduled "Install available updates" task when it is limited to marked packages.
/// </summary>
internal sealed class AutoUpdatesDialog : FormDialog
{
    private AutoUpdatesDialog() : base(CoreTools.Translate("Manage automatic updates"), 90, 32)
    {
        var packages = InstalledPackagesLoader.Instance.Packages
            .Where(p => !p.Source.IsVirtualManager)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Form.AddNote(CoreTools.Translate("Packages marked here are updated by the scheduled maintenance task when it is limited to marked packages."));
        if (packages.Count == 0) Form.AddNote(CoreTools.Translate("No packages were found"));
        foreach (var package in packages)
        {
            var p = package;
            string id = AutoUpdatesDatabase.GetIdForPackage(p);
            string suffix = IgnoredUpdatesDatabase.HasUpdatesIgnored(IgnoredUpdatesDatabase.GetIgnoredIdForPackage(p))
                ? "  (" + CoreTools.Translate("updates ignored") + ")"
                : "";
            Form.AddCheck($"{p.Name}  [{p.Manager.DisplayName}]{suffix}", () => AutoUpdatesDatabase.IsAutoUpdated(id), v =>
            {
                if (v) AutoUpdatesDatabase.Add(id);
                else AutoUpdatesDatabase.Remove(id);
            });
        }

        AddButton(CoreTools.Translate("Select all"), () =>
        {
            AutoUpdatesDatabase.AddRange(packages.Select(AutoUpdatesDatabase.GetIdForPackage));
            Form.Refresh();
        });
        AddButton(CoreTools.Translate("Clear selection"), () =>
        {
            AutoUpdatesDatabase.RemoveRange(packages.Select(AutoUpdatesDatabase.GetIdForPackage));
            Form.Refresh();
        });
        AddButton(CoreTools.Translate("Close"), () => Close(null));
    }

    public static Task ShowAsync() => TuiModal.ShowAsync(new AutoUpdatesDialog());
}

/// <summary>
/// "Manage shortcuts" (desktop <c>ManageShortcutsWindow</c>, desktop part): decide which desktop
/// shortcuts UniGetUI deletes automatically after installs and updates. Windows only.
/// </summary>
internal sealed class ShortcutsDialog : FormDialog
{
    private ShortcutsDialog() : base(CoreTools.Translate("Manage shortcuts"), 100, 30)
    {
        var shortcuts = DesktopShortcutsDatabase.GetAllShortcuts();
        Form.AddNote(CoreTools.Translate("Checked shortcuts will be deleted automatically when they are created again by an install or update."));
        if (shortcuts.Count == 0) Form.AddNote(CoreTools.Translate("No shortcuts were found"));
        foreach (string shortcut in shortcuts)
        {
            string s = shortcut;
            Form.AddCheck(s, () => DesktopShortcutsDatabase.GetStatus(s) is DesktopShortcutsDatabase.Status.Delete,
                v => DesktopShortcutsDatabase.AddToDatabase(s, v ? DesktopShortcutsDatabase.Status.Delete : DesktopShortcutsDatabase.Status.Maintain));
        }

        AddButton(CoreTools.Translate("Reset list"), () =>
        {
            DesktopShortcutsDatabase.ResetDatabase();
            Form.Refresh();
        });
        AddButton(CoreTools.Translate("Close"), () => Close(null));
    }

    public static Task ShowAsync()
    {
        if (!OperatingSystem.IsWindows())
            return TuiPrompts.InfoAsync(CoreTools.Translate("Manage shortcuts"), CoreTools.Translate("Shortcut management is only available on Windows."));
        if (TuiEngine.IsFakeData)
            return TuiPrompts.InfoAsync(CoreTools.Translate("Manage shortcuts"),
                CoreTools.Translate("Desktop shortcuts are real files on this machine, so they are not managed in fake data mode."));
        return TuiModal.ShowAsync(new ShortcutsDialog());
    }
}
