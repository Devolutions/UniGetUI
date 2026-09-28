// Interactive search multiselect prompt (port of prompts/search-multiselect.ts).
//
// Selection is tracked by item index in insertion order, mirroring the JS Set
// semantics that determine the order of the returned values. It draws with
// picocolors, like the original.

namespace Skills;

internal sealed class SearchItem<T>(T value, string key, string label)
{
    public T Value { get; } = value;

    /// `String(item.value)`, used for filtering.
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string? Hint { get; init; }
    public string? Group { get; init; }
    public string? Detail { get; init; }
}

internal sealed class LockedSection<T>
{
    public required string Title { get; init; }
    public required List<SearchItem<T>> Items { get; init; }
    public int HiddenCount { get; init; }
}

internal sealed class SearchMultiselectOptions<T>
{
    public required string Message { get; init; }
    public required List<SearchItem<T>> Items { get; init; }
    public int MaxVisible { get; set; } = 8;
    public List<int> InitialSelected { get; set; } = [];
    public bool Required { get; set; }
    public LockedSection<T>? Locked { get; set; }
    public bool Searchable { get; set; } = true;
    public bool ShowDetail { get; set; }
    public int DetailLines { get; set; } = 2;
    public bool ShowSelectedSummary { get; set; } = true;
    public bool SelectGroups { get; set; }
    public bool SelectAll { get; set; }
}

internal static class SearchMultiselect
{
    private abstract record Entry;

    private sealed record ItemEntry(int Index) : Entry;

    private sealed record GroupEntry(string Group, List<int> Items, bool Collapsed) : Entry;

    private static string TruncateToWidth(string text, int width)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var r in text.EnumerateRunes())
        {
            var candidate = sb + r.ToString();
            if (Ui.ApproxStringWidth(candidate) > width) break;
            sb.Append(r.ToString());
        }
        return sb.ToString();
    }

    /// Wrap description text into a fixed number of width-safe lines.
    public static List<string> FormatDetailLines(string? detail, int width, int maxLines)
    {
        var safeWidth = Math.Max(1, width);
        var normalized = System.Text.RegularExpressions.Regex.Replace(detail ?? "", @"\s+", " ").Trim();
        var lines = new List<string>();
        var remaining = normalized;
        while (remaining.Length > 0 && lines.Count < maxLines)
        {
            if (Ui.ApproxStringWidth(remaining) <= safeWidth)
            {
                lines.Add(remaining);
                remaining = "";
                break;
            }
            var candidate = TruncateToWidth(remaining, safeWidth);
            var breakAt = candidate.LastIndexOf(' ');
            if (breakAt > 0)
            {
                lines.Add(candidate[..breakAt].TrimEnd());
                remaining = remaining[breakAt..].TrimStart();
            }
            else
            {
                lines.Add(candidate);
                remaining = remaining[candidate.Length..].TrimStart();
            }
        }
        if (remaining.Length > 0 && lines.Count > 0)
            lines[^1] = TruncateToWidth(lines[^1], Math.Max(0, safeWidth - 1)).TrimEnd() + "…";
        while (lines.Count < maxLines) lines.Add("");
        return lines;
    }

    private static List<Entry> BuildEntries<T>(List<SearchItem<T>> items, List<int> filtered, bool selectGroups, ICollection<string> collapsed)
    {
        if (!selectGroups) return filtered.Select(i => (Entry)new ItemEntry(i)).ToList();
        var entries = new List<Entry>();
        var idx = 0;
        while (idx < filtered.Count)
        {
            var group = items[filtered[idx]].Group;
            if (group == null)
            {
                entries.Add(new ItemEntry(filtered[idx]));
                idx++;
                continue;
            }
            var groupItems = new List<int>();
            while (idx < filtered.Count && items[filtered[idx]].Group == group)
            {
                groupItems.Add(filtered[idx]);
                idx++;
            }
            var isCollapsed = collapsed.Contains(group);
            entries.Add(new GroupEntry(group, groupItems, isCollapsed));
            if (!isCollapsed) entries.AddRange(groupItems.Select(i => new ItemEntry(i)));
        }
        return entries;
    }

    private enum State
    {
        Active,
        Submit,
        Cancel,
    }

    private static string SBar => Pc.Dim("│");
    private static string SBarH => Pc.Dim("─");

    /// Run the prompt. Returns null when cancelled (the TS cancelSymbol).
    public static List<T>? Run<T>(SearchMultiselectOptions<T> o)
    {
        var items = o.Items;
        var query = "";
        var cursor = 0;
        var selected = new List<int>(); // insertion-ordered set
        foreach (var i in o.InitialSelected)
            if (!selected.Contains(i)) selected.Add(i);
        var collapsed = new List<string>();
        var lockedValues = o.Locked?.Items.Select(i => i.Value).ToList() ?? [];
        var hasSelectAll = o.SelectAll && items.Count > 0;
        var offset = hasSelectAll ? 1 : 0;

        List<int> GetFiltered(string q)
        {
            var lq = q.ToLowerInvariant();
            return Enumerable.Range(0, items.Count)
                .Where(i => q.Length == 0 || items[i].Label.ToLowerInvariant().Contains(lq) || items[i].Key.ToLowerInvariant().Contains(lq))
                .ToList();
        }

        List<string> Render(State state)
        {
            var lines = new List<string>();
            var filtered = GetFiltered(query);
            var entries = BuildEntries(items, filtered, o.SelectGroups, collapsed);
            var entryCursor = cursor - offset;
            var icon = state switch
            {
                State.Active => Pc.Green("◆"),
                State.Cancel => Pc.Red("■"),
                _ => Pc.Green("◇"),
            };
            lines.Add($"{icon}  {Pc.Bold(o.Message)}");

            if (state == State.Submit)
            {
                var labels = (o.Locked?.Items.Select(i => i.Label) ?? []).Concat(Enumerable.Range(0, items.Count).Where(selected.Contains).Select(i => items[i].Label));
                lines.Add($"{SBar}  {Pc.Dim(string.Join(", ", labels))}");
                return lines;
            }
            if (state == State.Cancel)
            {
                lines.Add($"{SBar}  {Pc.Strikethrough(Pc.Dim("Cancelled"))}");
                return lines;
            }

            if (o.Locked is { Items.Count: > 0 } locked)
            {
                lines.Add(SBar);
                var lockedTitle = $"{Pc.Bold(locked.Title)} {Pc.Dim("── always included")}";
                lines.Add($"{SBar}  {SBarH}{SBarH} {lockedTitle} {Ui.Repeat(SBarH, 12)}");
                foreach (var item in locked.Items) lines.Add($"{SBar}    {Pc.Green("•")} {Pc.Bold(item.Label)}");
                if (locked.HiddenCount > 0) lines.Add($"{SBar}    {Pc.Dim($"…and {locked.HiddenCount} more")}");
                lines.Add(SBar);
                lines.Add($"{SBar}  {SBarH}{SBarH} {Pc.Bold("Additional agents")} {Ui.Repeat(SBarH, 29)}");
            }
            if (o.Searchable)
            {
                lines.Add($"{SBar}  {Pc.Dim("Search:")} {query}{Pc.Inverse(" ")}");
                lines.Add($"{SBar}  {Pc.Dim("↑↓ move, space select, enter confirm")}");
                lines.Add(SBar);
            }
            if (hasSelectAll)
            {
                var count = Enumerable.Range(0, items.Count).Count(selected.Contains);
                var radio = count == items.Count ? Pc.Green("●") : count > 0 ? Pc.Yellow("◐") : Pc.Dim("○");
                var isCursor = cursor == 0;
                var prefix = isCursor ? Pc.Cyan("❯") : " ";
                var label = isCursor ? Pc.Underline(Pc.Bold("Select All")) : Pc.Bold("Select All");
                lines.Add($"{SBar} {prefix} {radio} {label} {Pc.Dim($"({count}/{items.Count})")}");
                lines.Add($"{SBar}   {Ui.Repeat(SBarH, 36)}");
            }

            var columns = Term.TerminalColumns() ?? 80;

            List<string> BuildFooter(bool includeDetail, bool includeSummary)
            {
                var footer = new List<string>();
                if (includeDetail)
                {
                    string? detail;
                    if (hasSelectAll && cursor == 0) detail = $"Select or clear all {items.Count} skills.";
                    else if (entryCursor >= 0 && entryCursor < entries.Count)
                        detail = entries[entryCursor] switch
                        {
                            GroupEntry g => $"Select all {g.Items.Count} skills in {g.Group}.",
                            ItemEntry ie => items[ie.Index].Detail,
                            _ => null,
                        };
                    else detail = null;
                    footer.Add(SBar);
                    footer.Add($"{SBar}  {Pc.Dim("Description")}");
                    foreach (var line in FormatDetailLines(detail, Math.Max(1, columns - 5), o.DetailLines)) footer.Add($"{SBar}  {Pc.Dim(line)}");
                }
                if (includeSummary)
                {
                    footer.Add(SBar);
                    var labels = (o.Locked?.Items.Select(i => i.Label) ?? []).Concat(Enumerable.Range(0, items.Count).Where(selected.Contains).Select(i => items[i].Label)).ToList();
                    if (labels.Count == 0) footer.Add($"{SBar}  {Pc.Dim("Selected: (none)")}");
                    else
                    {
                        var summary = labels.Count <= 3 ? string.Join(", ", labels) : $"{string.Join(", ", labels.Take(3))} +{labels.Count - 3} more";
                        footer.Add($"{SBar}  {Pc.Green("Selected:")} {summary}");
                    }
                }
                if (!o.Searchable)
                {
                    footer.Add(SBar);
                    footer.Add($"{SBar}  {Pc.Dim("↑↓ move, ←→ collapse/expand, space select, enter confirm")}");
                }
                footer.Add(Pc.Dim("└"));
                return footer;
            }

            List<string> BuildItems(int visibleLimit)
            {
                if (filtered.Count == 0) return [$"{SBar}  {Pc.Dim("No matches found")}"];
                var output = new List<string>();
                var start = Math.Max(0, Math.Min(entryCursor - visibleLimit / 2, entries.Count - visibleLimit));
                var end = Math.Min(entries.Count, start + visibleLimit);
                for (var i = start; i < end; i++)
                {
                    var isCursor = i == entryCursor;
                    switch (entries[i])
                    {
                        case GroupEntry g:
                        {
                            var count = g.Items.Count(selected.Contains);
                            var radio = count == g.Items.Count ? Pc.Green("●") : count > 0 ? Pc.Yellow("◐") : Pc.Dim("○");
                            var label = isCursor ? Pc.Underline(Pc.Bold(g.Group)) : Pc.Bold(g.Group);
                            var prefix = isCursor ? Pc.Cyan("❯") : " ";
                            output.Add($"{SBar} {prefix} {Pc.Dim(g.Collapsed ? "▸" : "▾")} {radio} {label}");
                            break;
                        }
                        case ItemEntry ie:
                        {
                            var item = items[ie.Index];
                            var radio = selected.Contains(ie.Index) ? Pc.Green("●") : Pc.Dim("○");
                            var label = isCursor ? Pc.Underline(item.Label) : item.Label;
                            var hint = item.Hint != null ? Pc.Dim($" ({item.Hint})") : "";
                            var prefix = isCursor ? Pc.Cyan("❯") : " ";
                            var groupItems = o.SelectGroups && item.Group != null ? filtered.Where(f => items[f].Group == item.Group).ToList() : [];
                            var tree = groupItems.Count > 0 ? $"{Pc.Dim(groupItems[^1] == ie.Index ? "└─" : "├─")} " : "";
                            output.Add($"{SBar} {prefix} {tree}{radio} {label}{hint}");
                            break;
                        }
                    }
                }
                var hiddenBefore = start;
                var hiddenAfter = entries.Count - end;
                if (hiddenBefore > 0 || hiddenAfter > 0)
                {
                    var parts = new List<string>();
                    if (hiddenBefore > 0) parts.Add($"↑ {hiddenBefore} more");
                    if (hiddenAfter > 0) parts.Add($"↓ {hiddenAfter} more");
                    output.Add($"{SBar}  {Pc.Dim(string.Join("  ", parts))}");
                }
                return output;
            }

            int? maxFrameRows = Term.TerminalRows() is { } rowsCount ? Math.Max(1, rowsCount - 1) : null;

            (List<string> Items, List<string> Footer, int Rows) Fit(bool includeDetail, bool includeSummary)
            {
                var footer = BuildFooter(includeDetail, includeSummary);
                var limit = Math.Max(1, o.MaxVisible);
                var itemLines = BuildItems(limit);
                int RowsOf(List<string> il) => Ui.CountVisualRows(lines.Concat(il).Concat(footer), columns);
                var frameRows = RowsOf(itemLines);
                while (maxFrameRows is { } max && frameRows > max && limit > 1)
                {
                    limit--;
                    itemLines = BuildItems(limit);
                    frameRows = RowsOf(itemLines);
                }
                return (itemLines, footer, frameRows);
            }

            var includeDetailPane = o.ShowDetail;
            var includeSummaryPane = o.ShowSelectedSummary;
            var fitted = Fit(includeDetailPane, includeSummaryPane);
            if (maxFrameRows is { } mfr)
            {
                if (fitted.Rows > mfr && includeDetailPane)
                {
                    includeDetailPane = false;
                    fitted = Fit(includeDetailPane, includeSummaryPane);
                }
                if (fitted.Rows > mfr && includeSummaryPane)
                {
                    includeSummaryPane = false;
                    fitted = Fit(includeDetailPane, includeSummaryPane);
                }
            }
            lines.AddRange(fitted.Items);
            lines.AddRange(fitted.Footer);
            return lines;
        }

        var frame = new Ui.Frame();
        if (!Ui.EnterRawMode())
        {
            // stdin is not interactive: behave like an EOF cancel.
            frame.Render(string.Join("\n", Render(State.Cancel)));
            return null;
        }

        frame.Render(string.Join("\n", Render(State.Active)));
        List<T>? result;
        while (true)
        {
            var key = Ui.ReadKey();
            var filtered = GetFiltered(query);
            var entries = BuildEntries(items, filtered, o.SelectGroups, collapsed);
            var entry = cursor >= offset && cursor - offset < entries.Count ? entries[cursor - offset] : null;
            var redraw = true;
            switch (key.Key)
            {
                case Key.Enter:
                    if (o.Required && selected.Count == 0 && lockedValues.Count == 0)
                    {
                        redraw = false;
                        break;
                    }
                    frame.Render(string.Join("\n", Render(State.Submit)));
                    result = lockedValues.Concat(selected.Select(i => items[i].Value)).ToList();
                    goto done;
                case Key.Escape or Key.CtrlC:
                    frame.Render(string.Join("\n", Render(State.Cancel)));
                    result = null;
                    goto done;
                case Key.Up:
                    cursor = Math.Max(0, cursor - 1);
                    break;
                case Key.Down:
                    cursor = Math.Min(Math.Max(0, entries.Count + offset - 1), cursor + 1);
                    break;
                case Key.Right when o.SelectGroups:
                    if (entry is GroupEntry { Collapsed: true } ge) collapsed.Remove(ge.Group);
                    else redraw = false;
                    break;
                case Key.Left when o.SelectGroups:
                {
                    var group = entry switch
                    {
                        GroupEntry g => g.Group,
                        ItemEntry ie => items[ie.Index].Group,
                        _ => null,
                    };
                    if (group == null)
                    {
                        redraw = false;
                        break;
                    }
                    if (!collapsed.Contains(group)) collapsed.Add(group);
                    var ce = BuildEntries(items, filtered, o.SelectGroups, collapsed);
                    var pos = ce.FindIndex(e => e is GroupEntry g && g.Group == group);
                    cursor = pos + offset;
                    break;
                }
                case Key.Space:
                    if (hasSelectAll && cursor == 0)
                    {
                        var all = Enumerable.Range(0, items.Count).All(selected.Contains);
                        for (var i = 0; i < items.Count; i++)
                        {
                            if (all) selected.Remove(i);
                            else if (!selected.Contains(i)) selected.Add(i);
                        }
                    }
                    else if (entry is GroupEntry g)
                    {
                        var all = g.Items.All(selected.Contains);
                        foreach (var i in g.Items)
                        {
                            if (all) selected.Remove(i);
                            else if (!selected.Contains(i)) selected.Add(i);
                        }
                    }
                    else if (entry is ItemEntry ie)
                    {
                        if (!selected.Remove(ie.Index)) selected.Add(ie.Index);
                    }
                    break;
                case Key.Backspace:
                    if (query.Length > 0) query = query[..^1];
                    cursor = 0;
                    break;
                case Key.Char when o.Searchable:
                    query += key.Ch;
                    cursor = 0;
                    break;
                default:
                    redraw = false;
                    break;
            }
            if (redraw) frame.Render(string.Join("\n", Render(State.Active)));
        }
        done:
        Ui.LeaveRawMode();
        return result;
    }
}
