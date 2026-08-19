using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// Tells whether the embedded browser (Avalonia's NativeWebView) can actually be hosted on
/// this machine, so the Help and Release Notes pages can show an "Open in browser" fallback
/// instead of embedding a control that blows up while initializing.
///
/// #5285: Avalonia picks the WebView2 (Chromium) backend only when it finds the Edge WebView2
/// runtime in the registry. When it doesn't, it silently falls back to the legacy EdgeHTML
/// backend (Windows.Web.UI.Interop.WebViewControl). That component was removed from Windows 10
/// together with legacy Edge, so creating it throws InvalidOperationException (0x80131509,
/// "Unexpected HRESULT ... from a call to a COM component"). The throw happens inside an
/// `async void` continuation deep in NativeWebViewControlHost, so nothing at the call site can
/// catch it: it reaches the dispatcher as an unhandled exception and kills the process.
/// Portable (zip) installs are the usual victims, since the installer is what normally brings
/// the WebView2 runtime along.
///
/// The detection below deliberately mirrors Avalonia's own ManagedWebView2Loader lookup, so
/// this class and Avalonia agree on which backend is about to be used.
/// </summary>
internal static class NativeWebViewSupport
{
    private const string EDGE_UPDATE_CLIENTSTATE_KEY = @"Software\Microsoft\EdgeUpdate\ClientState";
    private const string BROWSER_FOLDER_OVERRIDE_VAR = "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER";

    // EdgeUpdate channel identifiers, in the order Avalonia probes them.
    private static readonly string[] CHANNEL_GUIDS =
    [
        "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}", // Stable
        "{2CD8A007-E189-409D-A2C8-9AF4EF3C72AA}", // Beta
        "{0D50BFEC-CD6A-4F9A-964C-C7416E3ACB10}", // Dev
        "{65C35B14-6C1D-4122-AC46-7148CC9D6497}", // Canary
        "{BE59E8FD-089A-411B-A3B0-051D9E417818}", // Internal
    ];

    // Avalonia types that surface web view *initialization* failures: the two control hosts
    // that await the adapter from an `async void`, the factory that picks the backend, and
    // the Windows adapters themselves. The failing frames are the only reliable marker, since
    // the exception is a plain InvalidOperationException/COMException with no web view
    // specific type or message.
    //
    // Deliberately excludes plain `Avalonia.Controls.NativeWebView` frames: that class also
    // raises NavigationStarted/NavigationCompleted, so matching it would silently swallow
    // bugs in our own event handlers instead of letting them surface.
    private static readonly string[] WEBVIEW_FRAME_MARKERS =
    [
        "Avalonia.Controls.NativeWebViewControlHost",
        "Avalonia.Controls.NativeWebViewCompositorHost",
        "Avalonia.Controls.WebViewAdapter",
        "Avalonia.Controls.Win.WebView1",
        "Avalonia.Controls.Win.WebView2",
    ];

    private static bool? _isAvailable;

    // Set whenever the web view is unusable for a reason we cannot pin on a missing runtime,
    // so UnavailableReason stops offering remediation that may not apply.
    private static bool _causeUnknown;

    /// <summary>
    /// Raised (on the UI thread) when a web view that was believed to work failed to
    /// initialize, so pages currently showing one can switch to their fallback content.
    /// </summary>
    public static event Action? BecameUnavailable;

    /// <summary>
    /// False when embedding a web view is known to fail, in which case callers must not add
    /// a <see cref="Views.Controls.UniGetUiWebView"/> to the visual tree.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            if (_isAvailable is { } cached)
                return cached;

            bool available = DetectAvailability();
            _isAvailable = available;
            return available;
        }
    }

    /// <summary>
    /// Message explaining why the embedded browser is unavailable, ready to show to the user.
    /// </summary>
    public static string UnavailableReason
    {
        get
        {
            // A runtime failure, or a probe that could not complete, says nothing about what
            // is or isn't installed: those get the generic wording rather than the (possibly
            // wrong) "not installed" one.
            if (!_causeUnknown)
            {
                if (OperatingSystem.IsLinux())
                    return CoreTools.Translate("The built-in browser is not supported on Linux yet.");

                if (OperatingSystem.IsWindows())
                    return CoreTools.Translate("The built-in browser requires the Microsoft Edge WebView2 Runtime, which is not installed on this computer.");
            }

            return CoreTools.Translate("The built-in browser could not be loaded on this computer.");
        }
    }

    /// <summary>
    /// Records that the embedded browser cannot be used, so later navigations go straight to
    /// the fallback instead of retrying a code path that is known to crash.
    /// </summary>
    public static void MarkUnavailable()
    {
        if (_isAvailable is false)
            return;

        _isAvailable = false;
        _causeUnknown = true;

        try
        {
            BecameUnavailable?.Invoke();
        }
        catch (Exception ex)
        {
            // Called from the dispatcher's unhandled-exception handler: letting anything
            // escape here would defeat the point of catching the failure in the first place.
            Logger.Error("Could not switch the open page to its built-in-browser fallback");
            Logger.Error(ex);
        }
    }

    /// <summary>
    /// Whether <paramref name="exception"/> came out of web view initialization, and can
    /// therefore be swallowed instead of taking the process down.
    /// </summary>
    public static bool IsWebViewFailure(Exception? exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.Flatten().InnerExceptions)
                {
                    if (HasWebViewFrame(inner))
                        return true;
                }
            }

            if (HasWebViewFrame(current))
                return true;
        }

        return false;
    }

    private static bool HasWebViewFrame(Exception exception)
    {
        if (exception.StackTrace is not { Length: > 0 } trace)
            return false;

        foreach (string marker in WEBVIEW_FRAME_MARKERS)
        {
            if (trace.Contains(marker, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool DetectAvailability()
    {
        // WKWebView is part of the OS and always available.
        if (OperatingSystem.IsMacOS())
            return true;

        // WebKitGTK/WPE are not shipped with UniGetUI, and the Linux pages have always
        // offered the fallback instead.
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            bool found = HasWebView2Runtime();
            if (!found)
            {
                Logger.Warn("The Edge WebView2 runtime was not found; the built-in browser will be replaced " +
                            "by a link to the system browser (see issue #5285)");
            }

            return found;
        }
        catch (Exception ex)
        {
            // A failed probe must not be treated as "runtime present": embedding the web view
            // is the crashing path, showing the fallback is not. It is no proof of absence
            // either, though, so the user must not be told to go install something they may
            // well already have.
            _causeUnknown = true;
            Logger.Warn("Could not determine whether the Edge WebView2 runtime is installed");
            Logger.Warn(ex);
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasWebView2Runtime()
    {
        string? overrideFolder = Environment.GetEnvironmentVariable(BROWSER_FOLDER_OVERRIDE_VAR);
        if (!string.IsNullOrEmpty(overrideFolder) && File.Exists(GetLoaderPath(overrideFolder)))
            return true;

        foreach (RegistryHive hive in (ReadOnlySpan<RegistryHive>)[RegistryHive.LocalMachine, RegistryHive.CurrentUser])
        {
            foreach (string channelGuid in CHANNEL_GUIDS)
            {
                if (HasRuntimeInRegistry(hive, channelGuid))
                    return true;
            }
        }

        return false;
    }

    [SupportedOSPlatform("windows")]
    private static bool HasRuntimeInRegistry(RegistryHive hive, string channelGuid)
    {
        using RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry32);
        using RegistryKey? channel = root.OpenSubKey($@"{EDGE_UPDATE_CLIENTSTATE_KEY}\{channelGuid}");
        if (channel is null)
            return false;

        foreach (string valueName in channel.GetValueNames())
        {
            if (channel.GetValue(valueName) is not string { Length: > 0 } value)
                continue;

            bool pointsAtRuntime = valueName is "EBWebView"
                                   || (value.Contains("EBWebView", StringComparison.Ordinal) && Directory.Exists(value));

            if (pointsAtRuntime && File.Exists(GetLoaderPath(value)))
                return true;
        }

        return false;
    }

    private static string GetLoaderPath(string browserFolder)
        => Path.Combine(browserFolder, "EBWebView", RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm => "arm",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        }, "EmbeddedBrowserWebView.dll");
}
