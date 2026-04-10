using System.Diagnostics;
using ExternalLibraries.Clipboard;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UniGetUI.Core.Tools;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace UniGetUI.Interface.Pages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public abstract partial class BaseLogPage : IKeyboardShortcutListener, IEnterLeaveListener
    {
        protected int LOG_LEVEL = 4;

        protected abstract void LoadLogLevels();
        public abstract void LoadLog(bool isReload = false);

        public BaseLogPage(bool log_level_enabled)
        {
            InitializeComponent();
            LogLevelPane.Visibility = log_level_enabled ? Visibility.Visible : Visibility.Collapsed;
            if (log_level_enabled)
                LoadLogLevels();

            ActualThemeChanged += (_, _) => LoadLog();
        }

        protected void SelectLogLevelByName(string name)
        {
            LogLevelCombo.SelectedValue = name;
        }

        public void ReloadTriggered() => LoadLog(isReload: true);

        public void SelectAllTriggered() => LogTextBox.SelectAll();

        public void SearchTriggered() { }

        // Dark theme colors
        protected Color DARK_GREY = Color.FromArgb(255, 130, 130, 130);
        protected Color DARK_LIGHT_GREY = Color.FromArgb(255, 190, 190, 190);
        protected Color DARK_WHITE = Color.FromArgb(255, 250, 250, 250);
        protected Color DARK_YELLOW = Color.FromArgb(255, 255, 255, 90);
        protected Color DARK_RED = Color.FromArgb(255, 255, 80, 80);
        protected Color DARK_GREEN = Color.FromArgb(255, 80, 255, 80);
        protected Color DARK_BLUE = Color.FromArgb(255, 120, 120, 255);

        // Light theme colors
        protected Color LIGHT_GREY = Color.FromArgb(255, 125, 125, 225);
        protected Color LIGHT_LIGHT_GREY = Color.FromArgb(255, 50, 50, 150);
        protected Color LIGHT_WHITE = Color.FromArgb(255, 0, 0, 0);
        protected Color LIGHT_YELLOW = Color.FromArgb(255, 150, 150, 0);
        protected Color LIGHT_RED = Color.FromArgb(255, 205, 0, 0);
        protected Color LIGHT_GREEN = Color.FromArgb(255, 0, 205, 0);
        protected Color LIGHT_BLUE = Color.FromArgb(255, 0, 0, 205);

        public void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.SelectAll();
            WindowsClipboard.SetText(LogTextBox.SelectedText);
            LogTextBox.Select(LogTextBox.SelectionStart, LogTextBox.SelectionStart);
        }

        public void ExportButton_Click(object sender, RoutedEventArgs e) =>
            _ = _exportButton_Click();

        public async Task _exportButton_Click()
        {
            FileSavePicker savePicker = new()
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            WinRT.Interop.InitializeWithWindow.Initialize(
                savePicker,
                WinRT.Interop.WindowNative.GetWindowHandle(MainApp.Instance.MainWindow)
            );
            savePicker.FileTypeChoices.Add(CoreTools.Translate("Text"), [".txt"]);
            savePicker.SuggestedFileName = CoreTools.Translate("UniGetUI Log");

            StorageFile file = await savePicker.PickSaveFileAsync();
            if (file is not null)
            {
                LogTextBox.SelectAll();
                await File.WriteAllTextAsync(file.Path, LogTextBox.SelectedText);
                LogTextBox.Select(LogTextBox.SelectionStart, LogTextBox.SelectionStart);
                await CoreTools.ShowFileOnExplorer(file.Path);
            }
        }

        public void ReloadButton_Click(object sender, RoutedEventArgs e) => LoadLog();

        private void LogLevelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            LOG_LEVEL = LogLevelCombo.SelectedIndex + 1;
            LoadLog();
        }

        public void OnEnter() => LoadLog();

        public void OnLeave() => LogTextBox.Blocks.Clear();
    }
}
