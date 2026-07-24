using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using UniGetUI.Avalonia.ViewModels.Pages.LogPages;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.Controls;
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

        bool dark = ThemeHelper.IsDark;
        var defaultBrush = new SolidColorBrush(dark ? Color.FromRgb(250, 250, 250) : Color.FromRgb(0, 0, 0));
        var errorBrush = new SolidColorBrush(dark ? Color.FromRgb(255, 80, 80) : Color.FromRgb(205, 0, 0));

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

        var dialog = new Window
        {
            Width = 780,
            Height = 520,
            MinWidth = 460,
            MinHeight = 300,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = CoreTools.Translate("Operation log") + (target.Length > 0 ? $" — {target}" : ""),
        };

        var copyButton = new Button { Content = CoreTools.Translate("Copy to clipboard") };
        copyButton.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(dialog)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(plainText);
        };

        var exportButton = new Button { Content = CoreTools.Translate("Export to a file") };
        exportButton.Click += async (_, _) =>
        {
            var file = await dialog.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = CoreTools.Translate("Export log"),
                SuggestedFileName = CoreTools.Translate("UniGetUI Log"),
                FileTypeChoices = [new FilePickerFileType(CoreTools.Translate("Text")) { Patterns = ["*.txt"] }],
            });
            if (file is not null)
                await File.WriteAllTextAsync(file.Path.LocalPath, plainText);
        };

        var closeButton = new Button { Content = CoreTools.Translate("Close"), MinWidth = 100 };
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
            Background = Application.Current?.FindResource("ApplicationPageBackgroundThemeBrush") as IBrush,
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
        Grid.SetRow(footer, 2);

        dialog.Content = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 10,
            Children = { toolbar, editorBorder, footer },
        };

        await dialog.ShowDialog(owner);
    }
}
