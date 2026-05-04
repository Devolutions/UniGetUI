namespace UniGetUI.Avalonia.Infrastructure;

internal static class HeadlessModeOptions
{
    public const string HeadlessArgument = "--headless";

    public static bool IsHeadless(IReadOnlyList<string> args)
    {
        return args.Contains(HeadlessArgument, StringComparer.OrdinalIgnoreCase);
    }
}
