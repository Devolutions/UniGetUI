using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UniGetUI.Core.Data;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace UniGetUI.Interface.Dialogs
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class ReleaseNotes : Page, IDisposable
    {
        public event EventHandler<EventArgs>? Close;

        public ReleaseNotes()
        {
            InitializeComponent();
            _ = InitializeWebView();

            WebView.NavigationStarting += (_, _) =>
            {
                ProgressBar.Visibility = Visibility.Visible;
            };
            WebView.NavigationCompleted += (_, _) =>
            {
                ProgressBar.Visibility = Visibility.Collapsed;
            };
        }

        private async Task InitializeWebView()
        {
            await WebView.EnsureCoreWebView2Async();
            WebView.Source = new Uri(CoreData.GetGitHubReleasePageUrl());
        }

        public void Dispose()
        {
            WebView.Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close?.Invoke(this, EventArgs.Empty);
        }
    }
}
