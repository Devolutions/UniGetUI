using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
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
        SplashImage.Source = new Bitmap(AssetLoader.Open(new Uri(uri)));

        TaglineText.Text = CoreTools.Translate("Package management made easy");
        TaglineText.Foreground = isDark ? Brushes.White : Brushes.Black;
    }
}
