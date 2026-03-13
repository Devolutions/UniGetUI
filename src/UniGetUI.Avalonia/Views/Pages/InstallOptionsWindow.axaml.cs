using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UniGetUI.Avalonia.Models;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class InstallOptionsWindow : Window
{
    private readonly InstallOptionsEditorView? _editorView;

    public InstallOptionsWindow()
    {
        InitializeComponent();
        ApplyTranslations();
        SaveBtn.IsEnabled = false;
    }

    public InstallOptionsWindow(IPackage package, PackagePageMode pageMode)
    {
        _editorView = new InstallOptionsEditorView(package);
        InitializeComponent();
        ApplyTranslations();
        EditorHost.Content = _editorView;
    }

    private void ApplyTranslations()
    {
        SaveBtn.Content = CoreTools.Translate("Save");
        CancelBtn.Content = CoreTools.Translate("Cancel");
        Title = CoreTools.Translate("Install options");

        SaveBtn.Click += SaveBtn_OnClick;
        CancelBtn.Click += CancelBtn_OnClick;
    }

    private async void SaveBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_editorView is null)
        {
            Close();
            return;
        }

        await _editorView.SaveAsync();
        Close();
    }

    private void CancelBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

}
