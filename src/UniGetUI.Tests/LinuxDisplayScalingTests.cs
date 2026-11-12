using System.Buffers.Binary;
using System.Text;
using UniGetUI.Avalonia.Infrastructure;

namespace UniGetUI.Tests;

public sealed class LinuxDisplayScalingTests
{
    [Theory]
    // COSMIC at 150%: Xft/DPI in XSETTINGS, nothing in the X resources.
    [InlineData(147456, null, 1.5)]
    [InlineData(196608, null, 2.0)]
    // An Xft.dpi of 96 is what a desktop that does not manage the resource leaves behind.
    [InlineData(147456, 96.0, 1.5)]
    // XSETTINGS scaled further than the X resources: only the missing part is added.
    [InlineData(196608, 144.0, 1.333)]
    public void ComputeScaleCorrection_MakesUpWhatTheXResourcesMiss(int xsettingsDpi1024, double? xrdbDpi, double expected)
    {
        Assert.Equal(expected, LinuxDisplayScaling.ComputeScaleCorrection(xsettingsDpi1024, xrdbDpi));
    }

    [Theory]
    // GNOME, KDE and Xfce keep both sources in sync, so Avalonia already scales correctly.
    [InlineData(147456, 144.0)]
    [InlineData(98304, 96.0)]
    [InlineData(98304, null)]
    // Rounding noise between the two sources.
    [InlineData(147456, 143.5)]
    // A smaller XSETTINGS value is never used to shrink the app.
    [InlineData(98304, 144.0)]
    // No XSETTINGS manager.
    [InlineData(null, null)]
    [InlineData(null, 144.0)]
    [InlineData(0, null)]
    public void ComputeScaleCorrection_LeavesConsistentSetupsAlone(int? xsettingsDpi1024, double? xrdbDpi)
    {
        Assert.Null(LinuxDisplayScaling.ComputeScaleCorrection(xsettingsDpi1024, xrdbDpi));
    }

    [Theory]
    [InlineData("Xcursor.size:\t24\nXcursor.theme:\tPop\n", null)]
    [InlineData("Xcursor.size:\t24\nXft.dpi:\t144\nXft.antialias:\t1\n", 144.0)]
    [InlineData("Xft.dpi: 120.5", 120.5)]
    [InlineData("Xft.dpiExtra:\t200\n", null)]
    [InlineData("Xft.dpi:\tnot-a-number\n", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TryParseXftDpiResource_ReadsOnlyTheXftDpiResource(string? resources, double? expected)
    {
        Assert.Equal(expected, LinuxDisplayScaling.TryParseXftDpiResource(resources));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryParseXSettingsInteger_FindsXftDpiAmongOtherSettings(bool bigEndian)
    {
        byte[] data = new XSettingsBuilder(bigEndian)
            .AddString("Net/ThemeName", "Pop-dark")
            .AddColor("Gtk/Color")
            .AddInteger("Gdk/WindowScalingFactor", 1)
            .AddInteger("Xft/DPI", 147456)
            .AddInteger("Gtk/CursorThemeSize", 36)
            .Build();

        Assert.Equal(147456, LinuxDisplayScaling.TryParseXSettingsInteger(data, "Xft/DPI"));
        Assert.Equal(1, LinuxDisplayScaling.TryParseXSettingsInteger(data, "Gdk/WindowScalingFactor"));
        Assert.Null(LinuxDisplayScaling.TryParseXSettingsInteger(data, "Gdk/UnscaledDPI"));
    }

    [Fact]
    public void TryParseXSettingsInteger_RejectsTruncatedData()
    {
        byte[] data = new XSettingsBuilder(bigEndian: false)
            .AddString("Net/ThemeName", "Pop-dark")
            .AddInteger("Xft/DPI", 147456)
            .Build();

        for (int length = 0; length < data.Length; length++)
        {
            Assert.Null(LinuxDisplayScaling.TryParseXSettingsInteger(data.AsSpan(0, length), "Xft/DPI"));
        }
    }

    [Fact]
    public void HasUserScalingOverride_RespectsEveryVariableAvaloniaHonours()
    {
        Assert.False(LinuxDisplayScaling.HasUserScalingOverride(_ => null));
        Assert.True(LinuxDisplayScaling.HasUserScalingOverride(Only("AVALONIA_GLOBAL_SCALE_FACTOR", "2")));
        Assert.True(LinuxDisplayScaling.HasUserScalingOverride(Only("AVALONIA_SCREEN_SCALE_FACTORS", "DP-1=2")));
        Assert.True(LinuxDisplayScaling.HasUserScalingOverride(Only("AVALONIA_USE_PHYSICAL_DPI", "1")));
        Assert.True(LinuxDisplayScaling.HasUserScalingOverride(Only("QT_SCALE_FACTOR", "1.5")));
        Assert.True(LinuxDisplayScaling.HasUserScalingOverride(Only("QT_AUTO_SCREEN_SCALE_FACTOR", "1")));

        // Avalonia skips the Qt variables when asked to, so they are not an override then.
        Assert.False(LinuxDisplayScaling.HasUserScalingOverride(name => name switch
        {
            "AVALONIA_SCREEN_SCALE_IGNORE_QT" => "1",
            "QT_SCALE_FACTOR" => "1.5",
            _ => null,
        }));
    }

    private static Func<string, string?> Only(string variable, string value)
        => name => name == variable ? value : null;

    private sealed class XSettingsBuilder(bool bigEndian)
    {
        private readonly List<byte> _settings = [];
        private int _count;

        public XSettingsBuilder AddInteger(string name, int value)
        {
            AddHeader(0, name);
            AddUInt32((uint)value);
            return this;
        }

        public XSettingsBuilder AddString(string name, string value)
        {
            AddHeader(1, name);
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            AddUInt32((uint)bytes.Length);
            AddPadded(bytes);
            return this;
        }

        public XSettingsBuilder AddColor(string name)
        {
            AddHeader(2, name);
            _settings.AddRange(new byte[8]);
            return this;
        }

        public byte[] Build()
        {
            List<byte> data = [(byte)(bigEndian ? 1 : 0), 0, 0, 0];
            data.AddRange(UInt32Bytes(7)); // serial
            data.AddRange(UInt32Bytes((uint)_count));
            data.AddRange(_settings);
            return [.. data];
        }

        private void AddHeader(byte type, string name)
        {
            _count++;
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            byte[] nameLength = new byte[2];
            if (bigEndian)
                BinaryPrimitives.WriteUInt16BigEndian(nameLength, (ushort)nameBytes.Length);
            else
                BinaryPrimitives.WriteUInt16LittleEndian(nameLength, (ushort)nameBytes.Length);

            _settings.Add(type);
            _settings.Add(0);
            _settings.AddRange(nameLength);
            AddPadded(nameBytes);
            AddUInt32(3); // last-change serial
        }

        private void AddPadded(byte[] bytes)
        {
            _settings.AddRange(bytes);
            _settings.AddRange(new byte[((bytes.Length + 3) & ~3) - bytes.Length]);
        }

        private void AddUInt32(uint value) => _settings.AddRange(UInt32Bytes(value));

        private byte[] UInt32Bytes(uint value)
        {
            byte[] bytes = new byte[4];
            if (bigEndian)
                BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            else
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            return bytes;
        }
    }
}
