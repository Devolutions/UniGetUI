using Avalonia.Controls;
using Avalonia.Input;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Theme;
using UniGetUI.Tui.Views.Controls;

namespace UniGetUI.Tui.Views.Dialogs;

/// <summary>Help (desktop Help page): the keyboard reference, documentation and release-notes links, and the
/// command-line reference, in a scrollable dialog over the current page (F1).</summary>
internal sealed class HelpDialog : TuiDialog
{
    public const string HelpUrl = "https://github.com/Devolutions/UniGetUI";
    public const string CliDocsUrl = "https://github.com/Devolutions/UniGetUI/blob/main/docs/CLI.md";
    public const string IssuesUrl = "https://github.com/Devolutions/UniGetUI/issues/new/choose";

    private readonly TuiForm _form = new();
    private readonly Button _close;

    private HelpDialog()
        : base(CoreTools.Translate("Help"))
    {
        SetFrameWidth(104);
        SetFrameHeight(34);
        Body.Content = _form;

        _form.AddHeader(CoreTools.Translate("Keyboard shortcuts"));
        foreach (string line in new[]
                 {
                     "Alt+1 … Alt+9       go to the numbered page tab (Ctrl+1 … Ctrl+9 where the terminal sends it)",
                     "Ctrl+Tab            next page (Ctrl+Shift+Tab: previous)",
                     "Tab / Shift+Tab     move between the controls of the page",
                     "F10 / Alt+letter    menu bar (Alt+F File, Alt+P Page, …); ↑ ↓ Enter pick an item",
                     "F1                  help          F5 / Ctrl+R  reload",
                     "Ctrl+F or /         search        Ctrl+A  select all      Ctrl+Q  quit",
                     "Esc                 close a dialog or menu, or go back to the page's list",
                     "",
                     "Package lists:",
                     "  Space             select or unselect the package",
                     "  Enter             package details      Ctrl+Enter  main action",
                     "  o / Alt+Enter     installation options m  all actions for the package",
                     "  i u x             install / update / uninstall",
                     "  b                 add to bundle        g  ignore updates",
                     "  f s F3            filter by source / sort / search mode (or click the header chips)",
                     "",
                     "Dialogs:            Tab moves between fields, Enter activates, Esc cancels",
                     "Choice fields:      ← → change the value, Enter lists every value",
                 })
            _form.AddNote(line, TuiPalette.Text);

        _form.AddHeader(CoreTools.Translate("Documentation"));
        _form.AddButton(CoreTools.Translate("Open UniGetUI help") + $"  ({HelpUrl})", () => TuiPackageActions.OpenExternally(HelpUrl));
        _form.AddButton(CoreTools.Translate("Command-line reference") + $"  ({CliDocsUrl})", () => TuiPackageActions.OpenExternally(CliDocsUrl));
        _form.AddButton(CoreTools.Translate("Release notes") + $"  ({CoreData.ReleaseNotesUrl})", () => TuiPackageActions.OpenExternally(CoreData.ReleaseNotesUrl));
        _form.AddButton(CoreTools.Translate("Report an issue or submit a feature request"), () => TuiPackageActions.OpenExternally(IssuesUrl));
        _form.AddButton(CoreTools.Translate("Check for updates") + $"  ({CoreData.GetGitHubReleasePageUrl()})",
            () => TuiPackageActions.OpenExternally(CoreData.GetGitHubReleasePageUrl()));
        _form.AddNote(CoreTools.Translate("The terminal UI does not update itself; updates are installed with the desktop app or your package manager."));
        _form.AddHeader(CoreTools.Translate("Command line"));
        foreach (string line in TuiCommandLine.HelpText.Split('\n'))
            _form.AddNote(line.TrimEnd('\r'));
        _form.AddNote("Themes (--theme <id>):");
        foreach (TuiTheme theme in TuiThemes.All)
            _form.AddNote($"  {theme.Id,-30}  {theme.Name}");

        _close = AddButton(CoreTools.Translate("Close"), () => Close(null));
    }

    public static Task ShowAsync() => TuiModal.ShowAsync(new HelpDialog());

    // Open at the top of the reference with focus on Close: the first link sits further down and focusing it would
    // scroll there. While Close has focus, the arrows and PgUp/PgDn scroll the text.
    public override void FocusInitial() => _close.Focus();

    public override void OnPreviewKey(KeyEventArgs e)
    {
        if (!_close.IsFocused || e.KeyModifiers != KeyModifiers.None) return;
        double page = Math.Max(1, _form.ViewportRows - 1);
        double? delta = e.Key switch
        {
            Key.Up => -1,
            Key.Down => 1,
            Key.PageUp => -page,
            Key.PageDown => page,
            _ => null,
        };
        if (delta is not double lines) return;
        _form.ScrollBy(lines);
        e.Handled = true;
    }
}
