using System;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using UniGetUI.Core.Logging;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class BitmapLoader
{
    public static Bitmap? TryLoadAsset(string uri) => TryLoadAsset(new Uri(uri));

    public static Bitmap? TryLoadAsset(Uri uri)
    {
        try
        {
            using var stream = AssetLoader.Open(uri);
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not load bitmap asset {uri}");
            Logger.Warn(ex);
            return null;
        }
    }

    public static Bitmap LoadFile(string path)
    {
        using var stream = ReadFile(path);
        return new Bitmap(stream);
    }

    public static MemoryStream ReadFile(string path) => new(File.ReadAllBytes(path));
}
