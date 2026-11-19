using System.Diagnostics;
using System.Text;
using UniGetUI.Extensions.Drivers;

namespace UniGetUI.Extensions.Drivers.Tests;

public class DriverStandardInputTests
{
    [Fact]
    public void TheLoaderDecodesToTheStandardInputReader()
        => Assert.Equal(
            DriverScripts.StandardInputLoader,
            Encoding.Unicode.GetString(Convert.FromBase64String(DriverScripts.EncodedStandardInputLoader)));

    [Fact]
    public void TheCommandLineStaysShortWhateverTheNumberOfDrivers()
    {
        var elevation = new DriversElevation(false, false, @"C:\Program Files\gsudo\Current\gsudo.exe", []);

        ProcessStartInfo startInfo = PowerShellDriverScriptRunner.BuildStartInfo(elevation);

        int length = startInfo.FileName.Length + 3 + startInfo.ArgumentList.Sum(argument => argument.Length + 3);
        Assert.True(length < 1000, $"The command line is {length} characters long");
    }

    [Fact]
    public void TheScriptsAreAsciiSoTheConsoleCodePageCannotMangleThem()
    {
        string install = DriverScripts.Install([.. Enumerable.Range(0, 100).Select(_ => Guid.NewGuid().ToString())]);

        Assert.All(DriverScripts.Scan() + install, character => Assert.True(character < 128, $"'{character}' is not ASCII"));
    }

    [Fact]
    public void DriversThatMustBeInstalledAloneGetTheirOwnInstallation()
    {
        string install = DriverScripts.Install([Guid.NewGuid().ToString()]);

        Assert.Contains("[int]$update.InstallationBehavior.Impact -eq 2", install);
        Assert.Contains("foreach ($batch in $batches)", install);
    }

    [WindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheScriptsParseInWindowsPowerShell(bool install)
    {
        string script = install ? DriverScripts.Install([Guid.NewGuid().ToString(), Guid.NewGuid().ToString()]) : DriverScripts.Scan();
        var startInfo = new ProcessStartInfo(PowerShellDriverScriptRunner.PowerShellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "$errors = $null; [void][System.Management.Automation.Language.Parser]::ParseInput([Console]::In.ReadToEnd(), [ref]$null, [ref]$errors); $errors | ForEach-Object { $_.Message }; 'parse-errors:' + @($errors).Count" })
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        await process.StandardInput.WriteAsync(script);
        process.StandardInput.Close();
        await process.WaitForExitAsync();

        Assert.Contains("parse-errors:0", await output);
    }

    [WindowsFact]
    public async Task ALargeScriptRunsThroughCmdLikeAnElevatorRunsIt()
    {
        string script = "# " + new string('x', 40000) + "\n"
            + "try {\n    [Console]::Out.WriteLine('UNIGETUI-DRIVERS:' + (ConvertTo-Json -Compress @{ ok = 'yes' }))\n} catch {\n    [Console]::Out.WriteLine('failed')\n}\n";
        ProcessStartInfo inner = PowerShellDriverScriptRunner.BuildStartInfo(elevator: null);
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(inner.FileName);
        foreach (string argument in inner.ArgumentList) startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(script);
        process.StandardInput.Close();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, await error);
        Assert.Contains("UNIGETUI-DRIVERS:{\"ok\":\"yes\"}", await output);
    }
}
