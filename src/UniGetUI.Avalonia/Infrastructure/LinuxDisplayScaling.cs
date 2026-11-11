using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using UniGetUI.Core.Logging;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// Avalonia's X11 backend (used on Wayland desktops too, through XWayland) only reads the
/// display scale from the <c>Xft.dpi</c> X resource or from environment variables. Some
/// compositors publish the scale only through XSETTINGS: COSMIC, for instance, renders X11
/// clients at native resolution and sets <c>Xft/DPI</c> there, but never writes <c>Xft.dpi</c>
/// to the X resources. Avalonia then runs at 1x and the app looks tiny on HiDPI screens.
/// This bridges the gap by passing the missing factor to Avalonia before it starts.
/// </summary>
public static partial class LinuxDisplayScaling
{
    public const string GlobalScaleFactorVariable = "AVALONIA_GLOBAL_SCALE_FACTOR";

    private const double BaseDpi = 96.0;

    // A difference smaller than this is rounding noise between the two sources, not a real
    // scale the user picked (the smallest step desktops offer is 5%).
    private const double MinimumCorrection = 1.01;

    // Any of these means the user (or the desktop) already told Avalonia which scale to use.
    private static readonly string[] AvaloniaOverrideVariables =
    [
        GlobalScaleFactorVariable,
        "AVALONIA_SCREEN_SCALE_FACTORS",
        "AVALONIA_USE_PHYSICAL_DPI",
    ];

    // Avalonia honours the Qt variables too, unless told to ignore them.
    private static readonly string[] QtOverrideVariables =
    [
        "QT_SCALE_FACTOR",
        "QT_SCREEN_SCALE_FACTORS",
        "QT_AUTO_SCREEN_SCALE_FACTOR",
        "QT_USE_PHYSICAL_DPI",
    ];

    /// <summary>
    /// Sets <see cref="GlobalScaleFactorVariable"/> when XSETTINGS asks for a larger scale than
    /// the X resources give Avalonia. Must run before Avalonia initializes its X11 platform.
    /// </summary>
    public static void ApplyXSettingsScale()
    {
        if (!OperatingSystem.IsLinux() || HasUserScalingOverride(Environment.GetEnvironmentVariable))
        {
            return;
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return;
        }

        try
        {
            if (!TryReadDisplayDpis(out int? xsettingsDpi1024, out string? resources))
            {
                return;
            }

            double? xrdbDpi = TryParseXftDpiResource(resources);
            double? correction = ComputeScaleCorrection(xsettingsDpi1024, xrdbDpi);

            Logger.Info(
                $"Display DPI: XSETTINGS Xft/DPI={(xsettingsDpi1024 is { } x ? (x / 1024.0).ToString(CultureInfo.InvariantCulture) : "(unset)")}, "
                + $"Xft.dpi={(xrdbDpi?.ToString(CultureInfo.InvariantCulture) ?? "(unset)")}");

            if (correction is not { } factor)
            {
                return;
            }

            string value = factor.ToString("0.###", CultureInfo.InvariantCulture);
            Environment.SetEnvironmentVariable(GlobalScaleFactorVariable, value);
            Logger.Info($"Applying the desktop's XSETTINGS scale: {GlobalScaleFactorVariable}={value}");
        }
        catch (Exception ex)
        {
            // libX11 missing or an unexpected server reply: keep Avalonia's own detection.
            Logger.Warn($"Could not read the desktop's XSETTINGS scale: {ex.Message}");
        }
    }

    public static bool HasUserScalingOverride(Func<string, string?> getVariable)
    {
        if (AvaloniaOverrideVariables.Any(name => !string.IsNullOrEmpty(getVariable(name))))
        {
            return true;
        }

        if (getVariable("AVALONIA_SCREEN_SCALE_IGNORE_QT") == "1")
        {
            return false;
        }

        return QtOverrideVariables.Any(name => !string.IsNullOrEmpty(getVariable(name)));
    }

    /// <summary>
    /// Returns the factor to multiply Avalonia's scale by, or null when no correction is needed.
    /// Avalonia already applies <c>Xft.dpi / 96</c> and multiplies it by
    /// <see cref="GlobalScaleFactorVariable"/>, so only the part XSETTINGS adds on top is returned;
    /// desktops that keep both sources in sync (GNOME, KDE, Xfce) get no correction.
    /// </summary>
    /// <param name="xsettingsDpi1024">XSETTINGS <c>Xft/DPI</c>, which is in 1024ths of a DPI.</param>
    /// <param name="xrdbDpi">The <c>Xft.dpi</c> X resource.</param>
    public static double? ComputeScaleCorrection(int? xsettingsDpi1024, double? xrdbDpi)
    {
        if (xsettingsDpi1024 is not > 0)
        {
            return null;
        }

        double desiredScale = xsettingsDpi1024.Value / 1024.0 / BaseDpi;
        double avaloniaScale = xrdbDpi is > 0 ? xrdbDpi.Value / BaseDpi : 1.0;
        double correction = desiredScale / avaloniaScale;

        return correction >= MinimumCorrection ? Math.Round(correction, 3) : null;
    }

    /// <summary>Reads <c>Xft.dpi</c> from an X resource database string (RESOURCE_MANAGER).</summary>
    public static double? TryParseXftDpiResource(string? resources)
    {
        if (string.IsNullOrEmpty(resources))
        {
            return null;
        }

        foreach (string line in resources.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0 || line[..colon].Trim() != "Xft.dpi")
            {
                continue;
            }

            if (double.TryParse(line[(colon + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dpi))
            {
                return dpi;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads an integer setting from the <c>_XSETTINGS_SETTINGS</c> property, as laid out in the
    /// XSETTINGS specification (https://specifications.freedesktop.org/xsettings-spec/).
    /// </summary>
    public static int? TryParseXSettingsInteger(ReadOnlySpan<byte> data, string name)
    {
        const byte TypeInteger = 0;
        const byte TypeString = 1;
        const byte TypeColor = 2;

        if (data.Length < 12)
        {
            return null;
        }

        bool bigEndian = data[0] == 1; // MSBFirst
        uint ReadUInt32(ReadOnlySpan<byte> s) => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
        ushort ReadUInt16(ReadOnlySpan<byte> s) => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);
        static int Pad4(int n) => (n + 3) & ~3;

        uint count = ReadUInt32(data[8..]);
        int offset = 12;

        for (uint i = 0; i < count; i++)
        {
            if (offset + 4 > data.Length)
            {
                return null;
            }

            byte type = data[offset];
            int nameLength = ReadUInt16(data[(offset + 2)..]);
            offset += 4;

            if (offset + Pad4(nameLength) + 4 > data.Length)
            {
                return null;
            }

            string settingName = Encoding.ASCII.GetString(data.Slice(offset, nameLength));
            offset += Pad4(nameLength) + 4; // name, then last-change serial

            switch (type)
            {
                case TypeInteger:
                    if (offset + 4 > data.Length)
                    {
                        return null;
                    }

                    if (settingName == name)
                    {
                        return (int)ReadUInt32(data[offset..]);
                    }

                    offset += 4;
                    break;

                case TypeString:
                    if (offset + 4 > data.Length)
                    {
                        return null;
                    }

                    long valueLength = ReadUInt32(data[offset..]);
                    if (valueLength > data.Length)
                    {
                        return null;
                    }

                    offset += 4 + Pad4((int)valueLength);
                    break;

                case TypeColor:
                    offset += 8;
                    break;

                default:
                    return null;
            }
        }

        return null;
    }

    private static bool TryReadDisplayDpis(out int? xsettingsDpi1024, out string? resources)
    {
        xsettingsDpi1024 = null;
        resources = null;

        // Xlib requires XInitThreads before any other Xlib call in a process that uses it from
        // several threads, as Avalonia does. Avalonia calls it when its X11 platform starts, which
        // is after this probe; libX11 1.8 and later do it implicitly, but older ones do not.
        // Calling it again later does nothing.
        XInitThreads();

        nint display = XOpenDisplay(0);
        if (display == 0)
        {
            return false;
        }

        try
        {
            nint resourceString = XResourceManagerString(display);
            resources = resourceString == 0 ? null : Marshal.PtrToStringUTF8(resourceString);

            int screen = XDefaultScreen(display);
            nint selection = XInternAtom(display, $"_XSETTINGS_S{screen}", 0);
            nint owner = XGetSelectionOwner(display, selection);
            if (owner == 0)
            {
                return true; // No XSETTINGS manager running.
            }

            nint settingsAtom = XInternAtom(display, "_XSETTINGS_SETTINGS", 0);
            xsettingsDpi1024 = ReadXSettingsDpi(display, owner, settingsAtom);
            return true;
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    private static int _trappedXErrors;

    /// <summary>
    /// Reads Xft/DPI from the settings manager's window. The manager can exit or restart after
    /// <c>XGetSelectionOwner</c> returned its window, and the request then fails with BadWindow.
    /// Avalonia has not installed its X error handler yet, and libX11's default one ends the
    /// process, so errors are trapped here for the length of the request and a vanished manager
    /// reads as no settings.
    /// </summary>
    private static unsafe int? ReadXSettingsDpi(nint display, nint owner, nint settingsAtom)
    {
        _trappedXErrors = 0;
        nint previousHandler = XSetErrorHandler((nint)(delegate* unmanaged<nint, nint, int>)&TrapXError);
        nint property = 0;
        try
        {
            int status = XGetWindowProperty(
                display, owner, settingsAtom,
                0, MaxPropertyLongs, 0, settingsAtom,
                out _, out int format, out nuint itemCount, out _, out property);

            // Wait for the server to process the request, so an error it raises reaches the
            // handler above and not the one restored below.
            XSync(display, 0);

            if (_trappedXErrors > 0 || status != 0 || property == 0)
            {
                return null;
            }

            if (format != 8 || itemCount == 0 || itemCount > int.MaxValue)
            {
                return null;
            }

            byte[] data = new byte[(int)itemCount];
            Marshal.Copy(property, data, 0, data.Length);
            return TryParseXSettingsInteger(data, "Xft/DPI");
        }
        finally
        {
            if (property != 0)
            {
                XFree(property);
            }

            XSetErrorHandler(previousHandler);
        }
    }

    [UnmanagedCallersOnly]
    private static int TrapXError(nint display, nint errorEvent)
    {
        _trappedXErrors++;
        return 0;
    }

    private const string X11Library = "libX11.so.6";

    // In 32-bit units: 256 KiB, far more than any XSETTINGS manager publishes.
    private const nint MaxPropertyLongs = 0x10000;

    [LibraryImport(X11Library)]
    private static partial int XInitThreads();

    [LibraryImport(X11Library)]
    private static partial nint XSetErrorHandler(nint handler);

    [LibraryImport(X11Library)]
    private static partial int XSync(nint display, int discard);

    [LibraryImport(X11Library)]
    private static partial nint XOpenDisplay(nint displayName);

    [LibraryImport(X11Library)]
    private static partial int XCloseDisplay(nint display);

    [LibraryImport(X11Library)]
    private static partial int XDefaultScreen(nint display);

    [LibraryImport(X11Library)]
    private static partial nint XResourceManagerString(nint display);

    [LibraryImport(X11Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint XInternAtom(nint display, string atomName, int onlyIfExists);

    [LibraryImport(X11Library)]
    private static partial nint XGetSelectionOwner(nint display, nint selection);

    [LibraryImport(X11Library)]
    private static partial int XGetWindowProperty(
        nint display,
        nint window,
        nint property,
        nint longOffset,
        nint longLength,
        int delete,
        nint requestedType,
        out nint actualType,
        out int actualFormat,
        out nuint itemCount,
        out nuint bytesAfter,
        out nint data);

    [LibraryImport(X11Library)]
    private static partial int XFree(nint data);
}
