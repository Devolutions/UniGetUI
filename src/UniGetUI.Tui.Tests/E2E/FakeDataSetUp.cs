using NUnit.Framework;
using UniGetUI.Tui.FakeData;
using UniGetUI.Tui.Infrastructure;

namespace UniGetUI.Tui.Tests;

/// <summary>
/// Runs once before any UI test in this assembly: every UI test drives the real TUI against the fake
/// data set, with all UniGetUI state redirected into a throwaway sandbox. No real package manager is
/// ever called and nothing is installed.
/// </summary>
[SetUpFixture]
public sealed class FakeDataSetUp
{
    public static string Sandbox { get; private set; } = "";

    [OneTimeSetUp]
    public void Activate()
    {
        Sandbox = Path.Join(Path.GetTempPath(), "UniGetUI-TUI-Tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(FakePackageManagerProcess.StepDelayVariable, "15");
        FakeDataEnvironment.Activate(Sandbox);
        TuiBootstrapper.Initialize();
    }

    [OneTimeTearDown]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(Sandbox, recursive: true);
        }
        catch
        {
            // Best effort: a fake child process may still hold the state file for a moment.
        }
    }
}
