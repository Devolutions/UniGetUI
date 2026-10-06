using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace UniGetUI.Interface;

public static class WindowsConsoleHost
{
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint FileTypeDisk = 0x0001;
    private const uint FileTypePipe = 0x0003;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidHandle = 6;
    private const int ErrorInvalidParameter = 87;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static bool PrepareCliIO(bool allowAllocateIfNoParent = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (HasConsoleWindow() || HasRedirectedStandardHandles())
        {
            RebindStandardStreams();
            return true;
        }

        if (AttachConsole(AttachParentProcess) || (allowAllocateIfNoParent && AllocConsole()))
        {
            RebindStandardStreams();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Attaches to the parent console, including Windows Terminal's pseudoconsole, or allocates
    /// a console when the parent has none. Call only for interactive startup, before console
    /// redirection properties are queried; help and headless commands must use PrepareCliIO.
    /// </summary>
    /// <exception cref="IOException">
    /// Input or output is redirected, or console attachment replaced redirected standard error.
    /// Redirected managed streams remain available for reporting the startup error.
    /// </exception>
    /// <exception cref="Win32Exception">Console attachment or allocation failed.</exception>
    public static bool PrepareInteractiveIO()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        IntPtr errorHandle = GetStdHandle(StdErrorHandle);
        Stream? inputStream = HasRedirectedHandle(StdInputHandle) ? Console.OpenStandardInput() : null;
        Stream? outputStream = HasRedirectedHandle(StdOutputHandle) ? Console.OpenStandardOutput() : null;
        Stream? errorStream = HasRedirectedHandle(StdErrorHandle) ? Console.OpenStandardError() : null;

        if (!HasConsoleWindow() && !AttachConsole(AttachParentProcess))
        {
            int error = Marshal.GetLastWin32Error();
            // Access denied means this process is already attached, not that a new console is needed.
            if (error != ErrorAccessDenied)
            {
                if (error is not (ErrorInvalidHandle or ErrorInvalidParameter))
                {
                    RebindStandardStreams(inputStream, outputStream, errorStream);
                    throw new Win32Exception(error, "Failed to attach to the parent console.");
                }

                if (!AllocConsole())
                {
                    error = Marshal.GetLastWin32Error();
                    RebindStandardStreams(inputStream, outputStream, errorStream);
                    throw new Win32Exception(error, "Failed to allocate an interactive console.");
                }
            }
        }

        RebindStandardStreams(inputStream, outputStream, errorStream);

        // A pseudoconsole still exposes console handles to its attached clients, not its host pipes.
        if (inputStream is not null || outputStream is not null
            || Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            throw new IOException("The terminal UI requires console input and output. Remove stdin/stdout redirection or use a headless command.");
        }

        // AttachConsole normally honors STARTF_USESTDHANDLES. Do not silently lose an unusual
        // parent's stderr redirection when it omitted that flag.
        if (errorStream is not null && GetStdHandle(StdErrorHandle) != errorHandle)
        {
            throw new IOException("Console attachment replaced redirected standard error; interactive startup cannot safely continue.");
        }

        return true;
    }

    private static bool HasConsoleWindow()
    {
        return GetConsoleWindow() != IntPtr.Zero;
    }

    private static bool HasRedirectedStandardHandles()
    {
        return HasRedirectedHandle(StdInputHandle)
            || HasRedirectedHandle(StdOutputHandle)
            || HasRedirectedHandle(StdErrorHandle);
    }

    private static bool HasRedirectedHandle(int standardHandle)
    {
        IntPtr handle = GetStdHandle(standardHandle);
        if (handle == IntPtr.Zero || handle == InvalidHandleValue)
        {
            return false;
        }

        uint fileType = GetFileType(handle);
        return fileType is FileTypeDisk or FileTypePipe;
    }

    private static void RebindStandardStreams(
        Stream? inputStream = null,
        Stream? outputStream = null,
        Stream? errorStream = null)
    {
        Encoding utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.InputEncoding = utf8;
        Console.OutputEncoding = utf8;

        Console.SetIn(
            new StreamReader(
                inputStream ?? Console.OpenStandardInput(),
                utf8,
                detectEncodingFromByteOrderMarks: false
            )
        );
        Console.SetOut(new StreamWriter(outputStream ?? Console.OpenStandardOutput(), utf8) { AutoFlush = true });
        Console.SetError(new StreamWriter(errorStream ?? Console.OpenStandardError(), utf8) { AutoFlush = true });
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr hFile);
}
