using UniGetUI.Core.Tools;
using UniGetUI.Tui.Views.Controls;
using UniGetUI.Tui.Views.Pages;

namespace UniGetUI.Tui.Views.Dialogs;

/// <summary>A dialog whose body is a <see cref="TuiForm"/>.</summary>
internal class FormDialog : TuiDialog
{
    protected FormDialog(string title, double width = 80, double? height = null) : base(title, width)
    {
        Form = new TuiForm();
        Body.Content = Form;
        if (height is double h) SetFrameHeight(h);
    }

    protected TuiForm Form { get; }

    public override void FocusInitial() => Form.FocusPrimary();
}

/// <summary>Search mode / case / special characters / instant search for a package page.</summary>
internal sealed class SearchOptionsDialog : FormDialog
{
    private SearchOptionsDialog(PackageListPage page) : base(CoreTools.Translate("Search options"), 60)
    {
        PackageSearchMode mode = page.SearchMode;
        bool caseSensitive = page.CaseSensitive, ignoreSpecial = page.IgnoreSpecialCharacters, instant = page.InstantSearch;

        var modes = new List<TuiOption>
        {
            new(CoreTools.Translate("Both"), nameof(PackageSearchMode.Both)),
            new(CoreTools.Translate("Package Name"), nameof(PackageSearchMode.Name)),
            new(CoreTools.Translate("Package ID"), nameof(PackageSearchMode.Id)),
            new(CoreTools.Translate("Exact match"), nameof(PackageSearchMode.Exact)),
        };
        if (page.Kind == PackagePageKind.Discover)
            modes.Add(new(CoreTools.Translate("Show similar packages"), nameof(PackageSearchMode.Similar)));

        Form.AddChoice(CoreTools.Translate("Search mode"), modes, () => mode.ToString(), v => mode = Enum.Parse<PackageSearchMode>(v));
        Form.AddCheck(CoreTools.Translate("Case sensitive"), () => caseSensitive, v => caseSensitive = v);
        Form.AddCheck(CoreTools.Translate("Ignore special characters"), () => ignoreSpecial, v => ignoreSpecial = v);
        if (page.Kind != PackagePageKind.Discover)
            Form.AddCheck(CoreTools.Translate("Instant search"), () => instant, v => instant = v);
        AddButton(CoreTools.Translate("Apply"), () =>
        {
            page.SetSearchOptions(mode, caseSensitive, ignoreSpecial, instant);
            Close(true);
        });
        AddButton(CoreTools.Translate("Cancel"), () => Close(null));
    }

    public static Task ShowAsync(PackageListPage page) => TuiModal.ShowAsync(new SearchOptionsDialog(page));
}

/// <summary>Per-source visibility for a package page (the desktop "Filters" pane).</summary>
internal sealed class SourceFilterDialog : FormDialog
{
    private readonly List<(string Key, bool Visible)> _state;

    private SourceFilterDialog(IReadOnlyList<(string Label, string Key, bool Visible)> entries)
        : base(CoreTools.Translate("Filter by source"), 60, Math.Min(entries.Count + 8, 30))
    {
        _state = entries.Select(e => (e.Key, e.Visible)).ToList();
        for (int i = 0; i < entries.Count; i++)
        {
            int index = i;
            Form.AddCheck(entries[i].Label, () => _state[index].Visible, v => _state[index] = (_state[index].Key, v));
        }

        AddButton(CoreTools.Translate("Select all"), () =>
        {
            for (int i = 0; i < _state.Count; i++) _state[i] = (_state[i].Key, true);
            Form.Refresh();
        });
        AddButton(CoreTools.Translate("Clear selection"), () =>
        {
            for (int i = 0; i < _state.Count; i++) _state[i] = (_state[i].Key, false);
            Form.Refresh();
        });
        AddButton(CoreTools.Translate("Apply"), () => Close(_state.ToList()));
        AddButton(CoreTools.Translate("Cancel"), () => Close(null));
    }

    public static async Task<List<(string Key, bool Visible)>?> ShowAsync(IReadOnlyList<(string Label, string Key, bool Visible)> entries)
        => await TuiModal.ShowAsync(new SourceFilterDialog(entries)) as List<(string Key, bool Visible)>;
}
