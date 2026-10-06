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
        foreach (string line in GetKeyboardReference())
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
        _form.AddNote(CoreTools.Translate("Themes") + " (--theme <id>):");
        foreach (TuiTheme theme in TuiThemes.All)
            _form.AddNote($"  {theme.Id,-30}  {theme.Name}");

        _close = AddButton(CoreTools.Translate("Close"), () => Close(null));
    }

    internal static IReadOnlyList<string> GetKeyboardReference() =>
    [
        "Alt+1 … Alt+9       " + CoreTools.Translate("Go to the numbered page tab ({0} where the terminal sends it)", "Ctrl+1 … Ctrl+9"),
        "Ctrl+Tab            " + CoreTools.Translate("Next page ({0}: previous)", "Ctrl+Shift+Tab"),
        "Tab / Shift+Tab     " + CoreTools.Translate("Move between the controls of the page"),
        "F10 / Alt+" + CoreTools.Translate("letter") + "    " + CoreTools.Translate("Menu bar; use the highlighted access letter, then {0} to pick an item", "↑ ↓ Enter"),
        $"F1                  {CoreTools.Translate("Help")}          F5 / Ctrl+R  {CoreTools.Translate("Reload")}",
        $"Ctrl+F or /         {CoreTools.Translate("Search")}        Ctrl+A  {CoreTools.Translate("Select all")}      Ctrl+Q  {CoreTools.Translate("Quit")}",
        "Esc                 " + CoreTools.Translate("Close a dialog or menu, or return to the page's list"),
        "",
        CoreTools.Translate("Package lists") + ":",
        "  Space             " + CoreTools.Translate("Select or unselect the package"),
        $"  Enter             {CoreTools.Translate("Package details")}      Ctrl+Enter  {CoreTools.Translate("Main action")}",
        $"  o / Alt+Enter     {CoreTools.Translate("Installation options")} m  {CoreTools.Translate("All actions for the package")}",
        $"  i u x             {CoreTools.Translate("Install")} / {CoreTools.Translate("Update")} / {CoreTools.Translate("Uninstall")}",
        $"  b                 {CoreTools.Translate("Add to bundle")}        g  {CoreTools.Translate("Ignore updates")}",
        $"  f s F3            {CoreTools.Translate("Filter by source")} / {CoreTools.Translate("Sort")} / {CoreTools.Translate("Search mode")}",
        "",
        CoreTools.Translate("Dialogs") + ":            " + CoreTools.Translate("{0} moves between fields, {1} activates, {2} cancels", "Tab", "Enter", "Esc"),
        CoreTools.Translate("Choice fields") + ":      " + CoreTools.Translate("{0} changes the value, {1} lists every value", "← →", "Enter"),
    ];

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
