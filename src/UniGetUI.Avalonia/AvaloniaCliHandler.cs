using UniGetUI.Shared;

namespace UniGetUI.Avalonia;

internal static class AvaloniaCliHandler
{
    public const string DAEMON = "--daemon";
    public const string NO_CORRUPT_DIALOG = "--no-corrupt-dialog";

    public static int? HandlePreUiArgs(string[] args)
    {
        SharedPreUiCommandExitCodes exitCodes = OperatingSystem.IsWindows()
            ? SharedPreUiCommandDispatcher.WindowsCliExitCodes
            : SharedPreUiCommandDispatcher.PortableCliExitCodes;

        return SharedPreUiCommandDispatcher.TryHandle(
            args,
            exitCodes
        );
    }
}
