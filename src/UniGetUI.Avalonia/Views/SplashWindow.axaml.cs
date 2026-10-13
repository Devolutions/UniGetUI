using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();

        bool isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        string uri = isDark
            ? "avares://UniGetUI/Assets/SplashScreen.theme-dark.png"
            : "avares://UniGetUI/Assets/SplashScreen.png";
        SplashImage.Source = BitmapLoader.TryLoadAsset(uri);

        TaglineText.Text = CoreTools.Translate("Package management made easy");
        TaglineText.Foreground = isDark ? Brushes.White : Brushes.Black;
    }
}
