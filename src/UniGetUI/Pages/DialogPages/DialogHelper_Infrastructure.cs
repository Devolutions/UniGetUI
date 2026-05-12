using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface;
using UniGetUI.Interface.Dialogs;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using Windows.UI;
using Windows.UI.Text;

namespace UniGetUI.Pages.DialogPages;

public static partial class DialogHelper
{
    internal static class DialogFactory
    {
        public static ContentDialog Create()
        {
            var dialog = new ContentDialog()
            {
                XamlRoot = Window.MainContentGrid.XamlRoot,
                Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
            };
            return dialog;
        }

        public static ContentDialog Create_AsWindow(bool hasTitle, bool hasButtons = false)
        {
            var dialog = Create();
            dialog.Resources["ContentDialogMaxWidth"] = 8192;
            dialog.Resources["ContentDialogMaxHeight"] = 4096;
            dialog.SizeChanged += (_, _) =>
            {
                if (dialog.Content is FrameworkElement page)
                {
                    double maxW,
                        maxH;
                    int tresholdW = 1300,
                        tresholdH = 1300;
                    if (Window.NavigationPage.ActualWidth < tresholdW)
                        maxW = 100;
                    else if (Window.NavigationPage.ActualWidth >= tresholdW + 200)
                        maxW = 300;
                    else
                        maxW = Window.NavigationPage.ActualWidth - (tresholdW - 100);

                    if (Window.NavigationPage.ActualHeight < tresholdH)
                        maxH = (hasTitle ? 104 : 64) + (hasButtons ? 80 : 0);
                    else if (Window.NavigationPage.ActualHeight >= tresholdH + 200)
                        maxH = (hasTitle ? 320 : 280) + (hasButtons ? 80 : 0);
                    else
                        maxH =
                            Window.NavigationPage.ActualHeight
                            - (tresholdH - (hasTitle ? 120 : 80))
                            + (hasButtons ? 80 : 0);

                    page.Width = Math.Min(Math.Abs(Window.NavigationPage.ActualWidth - maxW), 8192);
                    page.Height = Math.Min(
                        Math.Abs(Window.NavigationPage.ActualHeight - maxH),
                        4096
                    );
                }
            };
            return dialog;
        }
    }

    public static MainWindow Window { get; set; } = null!;

    public struct LoadingDialog
    {
        public readonly int Id;
        public readonly string Title;
        public readonly string Text;
        private static readonly Random r = new();

        public LoadingDialog(string title, string text)
        {
            Title = title;
            Text = text;
            Id = r.Next();
        }
    }

    private static class NativeHelpers
    {
        [ComImport]
        [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDataTransferManagerInterop
        {
            IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
            void ShowShareUIForWindow(IntPtr appWindow);
        }

        public static readonly Guid _dtm_iid = new(
            0xa5caee9b,
            0x8708,
            0x49d1,
            0x8d,
            0x36,
            0x67,
            0xd2,
            0x5a,
            0x8d,
            0xa0,
            0x0c
        );
    }

    private static readonly List<LoadingDialog> _loadingDialogQueue = new();
    private static readonly List<ContentDialog> _dialogQueue = [];
    private static int _currentLoadingDialogId;
    private static ContentDialog? _currentLoadingDialog;

    public static int ShowLoadingDialog(string text) => ShowLoadingDialog(text, "");

    public static int ShowLoadingDialog(string title, string description)
    {
        var dialogData = new LoadingDialog(title, description);
        _loadingDialogQueue.Add(dialogData);
        _showNextLoadingDialogIfPossible();
        return dialogData.Id;
    }

    public static void HideLoadingDialog(int id)
    {
        _loadingDialogQueue.RemoveAll(d => d.Id == id);
        if (_currentLoadingDialogId == id)
        {
            _currentLoadingDialog?.Hide();
            _currentLoadingDialog = null;
            _currentLoadingDialogId = 0;
        }

        _showNextLoadingDialogIfPossible();
    }

    public static void HideAllLoadingDialogs()
    {
        _loadingDialogQueue.Clear();
        _currentLoadingDialog?.Hide();
        _currentLoadingDialog = null;
        _currentLoadingDialogId = 0;
    }

    public static void _showNextLoadingDialogIfPossible()
    {
        if (!_loadingDialogQueue.Any())
            return;
        var data = _loadingDialogQueue.First();

        if (Window.LoadingDialogCount == 0 && _dialogQueue.Count == 0)
        {
            _currentLoadingDialogId = data.Id;
            _currentLoadingDialog = DialogFactory.Create();
            _currentLoadingDialog.Title = data.Title;
            _currentLoadingDialog.Content = new StackPanel()
            {
                // Width = 400,
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Spacing = 20,
                Children =
                {
                    new TextBlock()
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        TextWrapping = TextWrapping.Wrap,
                        Text = data.Text,
                    },
                    new ProgressRing()
                    {
                        IsIndeterminate = true,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            };
            _ = ShowDialogAsync(_currentLoadingDialog, HighPriority: true);
        }
    }

    public static async Task<ContentDialogResult> ShowDialogAsync(
        ContentDialog dialog,
        bool HighPriority = false
    )
    {
        try
        {
            if (HighPriority && _dialogQueue.Count >= 1)
            {
                _dialogQueue.Insert(1, dialog);
            }
            else
            {
                _dialogQueue.Add(dialog);
            }

            while (_dialogQueue[0] != dialog)
            {
                await Task.Delay(100);
            }

            dialog.RequestedTheme = Window.MainContentGrid.RequestedTheme;
            Window.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
            ContentDialogResult result = await dialog.ShowAsync();
            Window.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            _dialogQueue.Remove(dialog);
            if (!_dialogQueue.Any())
                DialogHelper._showNextLoadingDialogIfPossible();
            return result;
        }
        catch (Exception e)
        {
            Logger.Error("An error occurred while showing a ContentDialog via ShowDialogAsync()");
            Logger.Error(e);
            _dialogQueue.Remove(dialog);
            return ContentDialogResult.None;
        }
    }

    public static async Task ShowIntegrityResult()
    {
        var dialog = DialogFactory.Create();

        dialog.Title = CoreTools.Translate("Integrity violation");
        dialog.Content = new ScrollView()
        {
            Content = new StackPanel()
            {
                Orientation = Orientation.Vertical,
                Spacing = 8,
                Children =
                {
                    new TextBlock()
                    {
                        Text =
                            CoreTools.Translate(
                                "UniGetUI or some of its components are missing or corrupt."
                            )
                            + " "
                            + CoreTools.Translate(
                                "It is strongly recommended to reinstall UniGetUI to adress the situation."
                            ),
                        FontWeight = new FontWeight(600),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground =
                            Application.Current.Resources["SystemControlErrorTextForegroundBrush"]
                            as Brush,
                    },
                    new TextBlock()
                    {
                        Text =
                            " ● "
                            + CoreTools.Translate(
                                "Refer to the UniGetUI Logs to get more details regarding the affected file(s)"
                            ),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock()
                    {
                        Text =
                            " ● "
                            + CoreTools.Translate(
                                "Integrity checks can be disabled from the Experimental Settings"
                            ),
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        string installerPath = Path.Join(
            CoreData.UniGetUIExecutableDirectory,
            "UniGetUI.Installer.exe"
        );
        if (File.Exists(installerPath))
        {
            dialog.SecondaryButtonText = CoreTools.Translate("Repair UniGetUI");
            dialog.DefaultButton = ContentDialogButton.Secondary;
            dialog.SecondaryButtonClick += (_, _) =>
            {
                Process.Start(installerPath, "/silent /NoDeployInstaller");
            };
        }

        dialog.PrimaryButtonText = CoreTools.Translate("Close");
        await ShowDialogAsync(dialog);
    }
}
