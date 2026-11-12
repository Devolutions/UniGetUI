using System.Buffers.Binary;
using System.Security;

namespace UniGetUI.Extensions.Xcode.Tests.Local;

/// <summary>Writes an app folder shaped like Xcode: Info.plist, version.plist and an executable header.</summary>
internal static class FakeXcodeBundle
{
    public const int Arm64 = 0x0100000C;
    public const int X64 = 0x01000007;

    public static string Create(string directory, string name, string version, string build,
        bool beta = false, string bundleId = "com.apple.dt.Xcode", int[]? cpus = null)
    {
        string app = Path.Combine(directory, name);
        string contents = Path.Combine(app, "Contents");
        Directory.CreateDirectory(Path.Combine(contents, "MacOS"));
        File.WriteAllText(Path.Combine(contents, "Info.plist"), Plist(
            ("CFBundleIdentifier", bundleId),
            ("CFBundleShortVersionString", version),
            ("CFBundleExecutable", "Xcode"),
            ("CFBundleIconName", beta ? "XcodeBeta" : "Xcode")));
        File.WriteAllText(Path.Combine(contents, "version.plist"), Plist(("ProductBuildVersion", build)));
        File.WriteAllBytes(Path.Combine(contents, "MacOS", "Xcode"), MachO(cpus ?? [Arm64]));
        return app;
    }

    public static string Plist(params (string Key, string Value)[] values) =>
        """<?xml version="1.0" encoding="UTF-8"?>""" + "\n"
        + """<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">""" + "\n"
        + "<plist version=\"1.0\"><dict>"
        + string.Concat(values.Select(v => $"<key>{SecurityElement.Escape(v.Key)}</key><string>{SecurityElement.Escape(v.Value)}</string>"))
        + "<key>NSNumber</key><integer>1</integer></dict></plist>";

    public static byte[] MachO(int[] cpus)
    {
        if (cpus.Length == 1)
        {
            byte[] thin = new byte[32];
            BinaryPrimitives.WriteUInt32LittleEndian(thin, 0xFEEDFACF);
            BinaryPrimitives.WriteInt32LittleEndian(thin.AsSpan(4), cpus[0]);
            return thin;
        }
        byte[] fat = new byte[8 + 20 * cpus.Length];
        BinaryPrimitives.WriteUInt32BigEndian(fat, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(4), (uint)cpus.Length);
        for (int i = 0; i < cpus.Length; i++)
            BinaryPrimitives.WriteInt32BigEndian(fat.AsSpan(8 + i * 20), cpus[i]);
        return fat;
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "unigetui-xcode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}
