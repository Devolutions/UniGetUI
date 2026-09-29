using System.Diagnostics;

string appPath = Path.Combine(
    AppContext.BaseDirectory,
    OperatingSystem.IsWindows() ? "UniGetUI.exe" : "UniGetUI"
);

try
{
    ProcessStartInfo startInfo = new(appPath)
    {
        UseShellExecute = false,
        WorkingDirectory = Environment.CurrentDirectory,
    };

    foreach (string arg in args)
    {
        startInfo.ArgumentList.Add(arg);
    }

    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"Could not start {appPath}.");
    process.WaitForExit();
    return process.ExitCode;
}
catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
{
    Console.Error.WriteLine($"uniget: {ex.Message}");
    return 1;
}
