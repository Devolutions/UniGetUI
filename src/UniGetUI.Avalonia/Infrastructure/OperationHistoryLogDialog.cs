using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using UniGetUI.Avalonia.ViewModels.Pages.LogPages;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.Controls;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Operations.History;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>Shows the full console output of a single history entry, with copy/export.</summary>
internal static class OperationHistoryLogDialog
{
    public static async Task ShowAsync(OperationHistoryRecord record)
    {
        if (MainWindow.Instance is not { } owner)
            return;

        ThemeVariant theme = ThemeHelper.Variant;
        IBrush defaultBrush = LookupBrush("TextFillColorPrimaryBrush", theme, Brushes.White);
        IBrush errorBrush = LookupBrush("StatusErrorForeground", theme, Brushes.Red);

        var lines = record.Output
            .Select(l => new LogLineItem(l.Text.Replace("\r", "").Replace("\n", ""),
                l.Type == "Error" ? errorBrush : defaultBrush))
            .ToList();
        if (lines.Count == 0)
            lines.Add(new LogLineItem(CoreTools.Translate("No output was recorded for this operation."), defaultBrush));

        string plainText = string.Join("\n", lines.Select(l => l.Text));
        string target = string.IsNullOrEmpty(record.PackageName) ? record.PackageId : record.PackageName;

        var editor = new LogTextEditor();
        editor.SetLines(lines);

        var dialog = new ImmersiveDialog
        {
            MaxWidth = 780,
            MaxHeight = 520,
            MinWidth = 460,
            MinHeight = 300,
            Title = CoreTools.Translate("Operation log") + (target.Length > 0 ? $" — {target}" : ""),
            TitleMargin = new Thickness(24, 0, 0, 0),
            Background = LookupBrush("AppDialogBackground", theme, Brushes.Transparent),
        };

        var copyButton = CreateDialogButton(CoreTools.Translate("Copy to clipboard"));
        copyButton.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(dialog)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(plainText);
        };

        var exportButton = CreateDialogButton(CoreTools.Translate("Export to a file"));
        exportButton.Click += async (_, _) =>
        {
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = CoreTools.Translate("Export log"),
                SuggestedFileName = CoreTools.Translate("UniGetUI Log"),
                FileTypeChoices = [new FilePickerFileType(CoreTools.Translate("Text")) { Patterns = ["*.txt"] }],
            });
            if (file is not null)
                await File.WriteAllTextAsync(file.Path.LocalPath, plainText);
        };

        var closeButton = CreateDialogButton(CoreTools.Translate("Close"), 100);
        closeButton.Classes.Remove("secondary-action");
        closeButton.Classes.Add("accent");
        closeButton.Click += (_, _) => dialog.Close();

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { copyButton, exportButton },
        };
        Grid.SetRow(toolbar, 0);

        var editorBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = LookupBrush("AppWindowBackground", theme, Brushes.Transparent),
            Child = editor,
        };
        Grid.SetRow(editorBorder, 1);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { closeButton },
        };

        var body = new Grid
        {
            Margin = new Thickness(24, 0, 24, 24),
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 10,
            Children = { toolbar, editorBorder },
        };
        Grid.SetRow(body, 0);

        var footerSurface = new Border
        {
            Background = LookupBrush("AppWindowBackground", theme, Brushes.Transparent),
            Padding = new Thickness(24),
            Child = footer,
        };
        Grid.SetRow(footerSurface, 1);

        dialog.Content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Children = { body, footerSurface },
        };

        await dialog.ShowDialog(owner);
    }

    private static IBrush LookupBrush(string key, ThemeVariant theme, IBrush fallback)
    {
        if (Application.Current?.TryGetResource(key, theme, out var resource) == true &&
            resource is IBrush brush)
            return brush;
        return fallback;
    }

    private static Button CreateDialogButton(string content, double minWidth = 0) => new()
    {
        Content = content,
        MinWidth = minWidth,
        Height = 32,
        Padding = new Thickness(11, 5, 11, 6),
        CornerRadius = new CornerRadius(4),
        FontSize = 14,
        Classes = { "secondary-action" },
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };
}
