using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace UniGetUI.Avalonia.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();

        bool isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        string uri = isDark
            ? "avares://UniGetUI.Avalonia/Assets/SplashScreen.theme-dark.png"
            : "avares://UniGetUI.Avalonia/Assets/SplashScreen.png";
        SplashImage.Source = new Bitmap(AssetLoader.Open(new Uri(uri)));
    }
}
