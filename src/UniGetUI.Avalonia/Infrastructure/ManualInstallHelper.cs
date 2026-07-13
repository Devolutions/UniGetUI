using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using Avalonia.Input.Platform;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// "Manual install/update/uninstall": generate the CLI command for a package operation and open
/// a terminal with the command pre-typed at the prompt, ready for the user to review and run by
/// hand. The command is also copied to the clipboard as a fallback. Shared by the package list
/// menus and the install-options dialog.
/// </summary>
internal static class ManualInstallHelper
{
    /// <summary>Builds the CLI command for a package (identical to the install-options preview),
    /// using its currently applicable options; null for virtual/local packages.</summary>
    private static async Task<string?> BuildCommandForPackageAsync(IPackage? package, OperationType operation)
    {
        if (package is null || package.Source.IsVirtualManager) return null;
        var options = await InstallOptionsFactory.LoadApplicableAsync(package);
        var args = await Task.Run(() => package.Manager.OperationHelper.GetParameters(package, options, operation));
        return package.Manager.Properties.ExecutableFriendlyName + " " + string.Join(' ', args);
    }

    /// <summary>Entry point for the "Manual install/update/uninstall" menu and toolbar actions.</summary>
    public static async Task LaunchManualAsync(IPackage? package, OperationType operation)
    {
        var command = await BuildCommandForPackageAsync(package, operation);
        if (!string.IsNullOrWhiteSpace(command))
            await LaunchManualAsync(command);
    }

    /// <summary>Copies the command to the clipboard and opens a terminal with it pre-typed at the prompt.</summary>
    public static async Task LaunchManualAsync(string command)
    {
        // Clipboard acts as a fallback in case the terminal cannot be pre-filled (e.g. non-Windows).
        await CopyToClipboardAsync(command);
        OpenPrefilledTerminal(command);
    }

    private static async Task CopyToClipboardAsync(string text)
    {
        if (MainWindow.Instance?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    /// <summary>
    /// Opens an interactive terminal with <paramref name="command"/> already typed at the prompt
    /// (but not executed), so the user can review and run it manually. Each platform uses its own
    /// native mechanism; on failure the command is still on the clipboard.
    /// </summary>
    private static void OpenPrefilledTerminal(string command)
    {
        try
        {
            if (OperatingSystem.IsWindows()) OpenWindowsTerminal(command);
            else if (OperatingSystem.IsMacOS()) OpenMacTerminal(command);
            else OpenLinuxTerminal(command);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not open a terminal for manual install; the command is on the clipboard");
            Logger.Warn(ex);
        }
    }

    // Windows PowerShell injects the command into its own console input buffer (WriteConsoleInput)
    // so PSReadLine shows it pre-typed at the first prompt; -NoExit keeps the window open.
    private static void OpenWindowsTerminal(string command)
    {
        string literal = command.Replace("'", "''");
        string script = "$ErrorActionPreference = 'SilentlyContinue'\n"
                      + "$cmd = '" + literal + "'\n"
                      + CONSOLE_INJECTOR
                      + "[UniGetUIConsoleInjector]::Type($cmd)\n";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        Process.Start(new ProcessStartInfo
        {
            FileName = CoreData.PowerShell5,
            Arguments = $"-NoExit -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = true,
        });
    }

    // macOS: zsh's 'print -z' pushes text onto the line-editor buffer so it appears pre-typed at the
    // next prompt without executing. We seed it from a throwaway startup file (ZDOTDIR) and open
    // Terminal.app on a launcher script via `open`. This deliberately avoids AppleScript's `do script`,
    // which sends an Apple event that requires Automation (TCC) permission the app usually lacks; when
    // denied it fails silently, so the terminal opens (from `activate`) but the command is never typed.
    [SupportedOSPlatform("macos")]
    private static void OpenMacTerminal(string command)
    {
        string dir = Path.Combine(Path.GetTempPath(), "unigetui-manual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        // ' is escaped for the enclosing single-quoted zsh argument. Setting ZDOTDIR bypasses the
        // user's normal startup files, so re-source them before seeding the buffer; then unset it so
        // any shells the user spawns behave normally.
        string cmdLiteral = command.Replace("'", "'\\''");
        File.WriteAllText(Path.Combine(dir, ".zshrc"),
            "[ -f \"$HOME/.zshenv\" ] && source \"$HOME/.zshenv\"\n"
            + "[ -f \"$HOME/.zshrc\" ] && source \"$HOME/.zshrc\"\n"
            + "unset ZDOTDIR\n"
            + "print -z -- '" + cmdLiteral + "'\n");

        // Launcher: re-exec an interactive zsh whose startup files come from our temp dir. Terminal
        // runs a *.command file passed to `open` as long as it is executable.
        string launcher = Path.Combine(dir, "launch.command");
        string dirLiteral = dir.Replace("'", "'\\''");
        File.WriteAllText(launcher,
            "#!/bin/zsh\n"
            + "ZDOTDIR='" + dirLiteral + "' exec zsh -i\n");
        File.SetUnixFileMode(launcher,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Process.Start(new ProcessStartInfo
        {
            FileName = "open",
            UseShellExecute = false,
            ArgumentList = { "-a", "Terminal", launcher },
        });
    }

    // Linux: launch the first available terminal emulator running bash, which seeds the command into
    // its readline buffer without executing it (see BuildBashPrefillRcFile).
    private static void OpenLinuxTerminal(string command)
    {
        string rcfile = BuildBashPrefillRcFile(command);
        foreach (var (exe, prefix) in LinuxTerminals)
        {
            string? full = FindOnPath(exe);
            if (full is null) continue;

            var psi = new ProcessStartInfo { FileName = full, UseShellExecute = false };
            foreach (var arg in prefix) psi.ArgumentList.Add(arg);
            psi.ArgumentList.Add("bash");
            psi.ArgumentList.Add("--rcfile");
            psi.ArgumentList.Add(rcfile);
            psi.ArgumentList.Add("-i");
            Process.Start(psi);
            return;
        }
        Logger.Warn("No supported terminal emulator was found; the command is on the clipboard");
    }

    // Candidate terminal emulators (in order of preference) and the argument(s) after which the
    // program to run is passed as argv.
    private static readonly (string Exe, string[] Prefix)[] LinuxTerminals =
    [
        ("gnome-terminal", ["--"]),
        ("konsole", ["-e"]),
        ("kitty", []),
        ("alacritty", ["-e"]),
        ("wezterm", ["start", "--"]),
        ("xfce4-terminal", ["-x"]),
        ("xterm", ["-e"]),
    ];

    private static string? FindOnPath(string exe)
    {
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (pathVar is null) return null;
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            string candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // Writes a temporary bash rcfile that seeds the command into the readline buffer without running
    // it: bind a macro to the terminal's Device-Status-Report reply (ESC[0n), then request a report
    // (ESC[5n); readline inserts the macro text at the first prompt.
    private static string BuildBashPrefillRcFile(string command)
    {
        // \ and " are escaped for the readline macro; ' is escaped for the enclosing single-quoted
        // bash argument (order matters: ' must be last so its backslashes aren't doubled).
        string escaped = command
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("'", "'\\''");
        string rcfile = Path.Combine(Path.GetTempPath(), "unigetui-manual-" + Guid.NewGuid().ToString("N") + ".bashrc");
        File.WriteAllText(rcfile,
            "[ -f ~/.bashrc ] && source ~/.bashrc\n"
            + "bind '\"\\e[0n\": \"" + escaped + "\"'\n"
            + "printf '\\e[5n'\n");
        return rcfile;
    }

    private const string CONSOLE_INJECTOR = """
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class UniGetUIConsoleInjector {
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    static extern bool WriteConsoleInputW(IntPtr h, INPUT_RECORD[] r, uint n, out uint w);
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT_RECORD { [FieldOffset(0)] public ushort EventType; [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    public struct KEY_EVENT_RECORD { public int bKeyDown; public ushort wRepeatCount; public ushort wVirtualKeyCode; public ushort wVirtualScanCode; public char UnicodeChar; public uint dwControlKeyState; }
    public static void Type(string text) {
        IntPtr h = GetStdHandle(-10);
        var recs = new INPUT_RECORD[text.Length * 2];
        int i = 0;
        foreach (char c in text) {
            recs[i].EventType = 1; recs[i].KeyEvent = new KEY_EVENT_RECORD { bKeyDown = 1, wRepeatCount = 1, UnicodeChar = c }; i++;
            recs[i].EventType = 1; recs[i].KeyEvent = new KEY_EVENT_RECORD { bKeyDown = 0, wRepeatCount = 1, UnicodeChar = c }; i++;
        }
        uint w; WriteConsoleInputW(h, recs, (uint)recs.Length, out w);
    }
}
"@

""";
}
