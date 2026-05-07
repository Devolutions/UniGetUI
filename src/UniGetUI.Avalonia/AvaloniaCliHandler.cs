using UniGetUI.Shared;

namespace UniGetUI.Avalonia;

internal static class AvaloniaCliHandler
{
    public const string DAEMON = "--daemon";
    public const string NO_CORRUPT_DIALOG = "--no-corrupt-dialog";

    public static int? HandlePreUiArgs(string[] args)
    {
        return SharedPreUiCommandDispatcher.TryHandle(
            args,
            SharedPreUiCommandDispatcher.AvaloniaExitCodes
        );
    }
}
