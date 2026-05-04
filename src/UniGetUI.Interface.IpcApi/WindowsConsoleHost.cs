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

    private static void RebindStandardStreams()
    {
        Encoding utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.InputEncoding = utf8;
        Console.OutputEncoding = utf8;

        Console.SetIn(
            new StreamReader(
                Console.OpenStandardInput(),
                utf8,
                detectEncodingFromByteOrderMarks: false
            )
        );
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
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
