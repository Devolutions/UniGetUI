namespace UniGetUI.Extensions.Xcode.Tests;

/// <summary>A shell script standing in for xcodes, in a folder of its own that is removed afterwards.</summary>
internal sealed class FakeXcodesScript : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xcodes-tests-").FullName;

    public FakeXcodesScript(string body, string name = "xcodes")
    {
        Path = System.IO.Path.Combine(_directory, name);
        File.WriteAllText(Path, "#!/bin/bash\n" + body.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public string Path { get; }

    /// <summary>The folder holding the script, for putting on a search path.</summary>
    public string Folder => _directory;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
