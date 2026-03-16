using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Octokit;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Widgets;
using UniGetUI.Pages.DialogPages;
using UniGetUI.Pages.SettingsPages.GeneralPages;
using Windows.UI;

namespace UniGetUI.Services
{
    public partial class PointButton : Button
    {
        public PointButton()
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
        }
    }

    public partial class UserAvatar : UserControl
    {
        public UserAvatar()
        {
            VerticalContentAlignment = VerticalAlignment.Center;
            HorizontalContentAlignment = HorizontalAlignment.Center;
            _ = RefreshStatus();
            GitHubAuthService.AuthStatusChanged += GitHubAuthService_AuthStatusChanged;
        }

        private void GitHubAuthService_AuthStatusChanged(object? sender, EventArgs e)
        {
            _ = RefreshStatus();
        }

        public async Task RefreshStatus()
        {
            SetLoading();
            var client = new GitHubAuthService();
            // await Task.Delay(1000);
            if (client.IsAuthenticated())
            {
                Content = await GenerateLogoutControl();
            }
            else
            {
                Content = GenerateLoginControl();
            }
        }

        private void LoginButton_Click(object sender, RoutedEventArgs e) =>
            _ = _loginButton_Click();

        private async Task _loginButton_Click()
        {
            SetLoading();
            try
            {
                var client = new GitHubAuthService();
                if (client.IsAuthenticated())
                {
                    Logger.Warn("Login invoked when the client was already logged in!");
                    return;
                }

                bool success = await client.SignInAsync();
                if (!success)
                {
                    DialogHelper.ShowDismissableBalloon(
                        CoreTools.Translate("Failed"),
                        CoreTools.Translate("An error occurred while logging in: ")
                    );
                }
            }
            catch (Exception ex)
            {
                DialogHelper.ShowDismissableBalloon(
                    CoreTools.Translate("Error"),
                    CoreTools.Translate("Log in failed: ") + ex.Message
                );
            }
        }

        private void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            SetLoading();
            try
            {
                var client = new GitHubAuthService();
                if (client.IsAuthenticated())
                {
                    client.SignOut();
                }
            }
            catch (Exception ex)
            {
                DialogHelper.ShowDismissableBalloon(
                    CoreTools.Translate("Error"),
                    CoreTools.Translate("Log out failed: ") + ex.Message
                );
            }
        }

        private void SetLoading()
        {
            this.Content = new ProgressRing()
            {
                IsIndeterminate = true,
                Width = 24,
                Height = 24,
            };
        }

        private PointButton GenerateLoginControl()
        {
            var personPicture = new PersonPicture { Width = 36, Height = 36 };

            var translatedTextBlock = new TextBlock
            {
                Margin = new Thickness(4),
                TextWrapping = TextWrapping.Wrap,
                Text = CoreTools.Translate("Log in with GitHub to enable cloud package backup."),
            };

            var hyperlinkButton = new HyperlinkButton
            {
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new TextBlock()
                {
                    Text = CoreTools.Translate("More details"),
                    TextWrapping = TextWrapping.Wrap,
                },
                FontSize = 12,
            };
            hyperlinkButton.Click += (_, _) =>
                CoreTools.Launch("https://devolutions.net/unigetui");

            var loginButton = new PointButton
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = CoreTools.Translate("Log in"),
            };
            loginButton.Click += LoginButton_Click;

            var stackPanel = new StackPanel
            {
                MaxWidth = 200,
                Margin = new Thickness(-8),
                Orientation = Orientation.Vertical,
                Spacing = 8,
            };
            stackPanel.Children.Add(translatedTextBlock);
            stackPanel.Children.Add(hyperlinkButton);
            stackPanel.Children.Add(loginButton);

            var flyout = new BetterFlyout()
            {
                LightDismissOverlayMode = LightDismissOverlayMode.Off,
                Placement = FlyoutPlacementMode.Bottom,
                Content = stackPanel,
            };

            return new PointButton
            {
                Margin = new Thickness(0),
                Padding = new Thickness(4),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(100),
                Content = personPicture,
                Flyout = flyout,
            };
        }

        private async Task<PointButton> GenerateLogoutControl()
        {
            await Task.Run(CoreTools.WaitForInternetConnection);
            User user;
            try
            {
                var authClient = new GitHubAuthService();
                var GHClient = authClient.CreateGitHubClient();
                if (GHClient is null)
                {
                    Logger.Error("Client did not report valid authentication");
                    return GenerateLoginControl();
                }

                user = await GHClient.User.Current();
            }
            catch (Exception ex)
            {
                Logger.Error("An error occurred while retrieving user's logged in data.");
                Logger.Error(ex);
                return GenerateLoginControl();
            }

            var personPicture = new PersonPicture
            {
                Width = 36,
                Height = 36,
                ProfilePicture = new BitmapImage(new Uri(user.AvatarUrl)),
            };

            var text1 = new TextBlock
            {
                Margin = new Thickness(4),
                TextWrapping = TextWrapping.Wrap,
                Text = CoreTools.Translate(
                    "You are logged in as {0} (@{1})",
                    user.Name,
                    user.Login
                ),
            };

            var text2 = new TextBlock
            {
                Margin = new Thickness(4),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                FontWeight = new(500),
                Text = CoreTools.Translate(
                    "If you have cloud backup enabled, it will be saved as a GitHub Gist on this account"
                ),
            };

            var hyperlinkButton = new HyperlinkButton
            {
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new TextBlock()
                {
                    Text = CoreTools.Translate("More details"),
                    TextWrapping = TextWrapping.Wrap,
                },
                FontSize = 12,
            };
            hyperlinkButton.Click += (_, _) =>
                CoreTools.Launch("https://devolutions.net/unigetui");

            var hyperlinkButton2 = new HyperlinkButton
            {
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new TextBlock()
                {
                    Text = CoreTools.Translate("Package backup settings"),
                    TextWrapping = TextWrapping.Wrap,
                },
                FontSize = 12,
            };
            hyperlinkButton2.Click += (_, _) =>
                MainApp.Instance.MainWindow.NavigationPage.OpenSettingsPage(typeof(Backup));

            var loginButton = new PointButton
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = CoreTools.Translate("Log out"),
                Background = new SolidColorBrush(
                    ActualTheme is ElementTheme.Dark ? Colors.DarkRed : Colors.PaleVioletRed
                ),
                BorderThickness = new(0),
            };
            loginButton.Click += LogoutButton_Click;

            var stackPanel = new StackPanel
            {
                MaxWidth = 200,
                Margin = new Thickness(-8),
                Orientation = Orientation.Vertical,
                Spacing = 8,
            };
            stackPanel.Children.Add(text1);
            stackPanel.Children.Add(text2);
            stackPanel.Children.Add(hyperlinkButton);
            stackPanel.Children.Add(hyperlinkButton2);
            stackPanel.Children.Add(loginButton);

            var flyout = new BetterFlyout()
            {
                LightDismissOverlayMode = LightDismissOverlayMode.Off,
                Placement = FlyoutPlacementMode.Bottom,
                Content = stackPanel,
            };

            return new PointButton
            {
                Margin = new Thickness(0),
                Padding = new Thickness(4),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(100),
                Content = personPicture,
                Flyout = flyout,
            };
        }
    }
}
