using System;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using UniGetUI.Core.Logging;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class AssetBitmapLoader
{
    public static Bitmap? TryLoad(string uri)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(uri));
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not load bitmap asset {uri}");
            Logger.Warn(ex);
            return null;
        }
    }
}
