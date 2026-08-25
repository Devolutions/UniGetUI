using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

public sealed class TestProcessTaskLogger : IProcessTaskLogger
{
    public List<string> StdOut { get; } = [];
    public List<string> StdErr { get; } = [];
    public List<string> StdIn { get; } = [];
    public int? ReturnCode { get; private set; }

    public void AddToStdOut(IReadOnlyList<string> lines) => StdOut.AddRange(lines);

    public void AddToStdOut(string? line)
    {
        if (line is not null)
            StdOut.Add(line);
    }

    public void AddToStdErr(IReadOnlyList<string> lines) => StdErr.AddRange(lines);

    public void AddToStdErr(string? line)
    {
        if (line is not null)
            StdErr.Add(line);
    }

    public void AddToStdIn(IReadOnlyList<string> lines) => StdIn.AddRange(lines);

    public void AddToStdIn(string? line)
    {
        if (line is not null)
            StdIn.Add(line);
    }

    public IReadOnlyList<string> AsColoredString(bool verbose = false) => [];

    public void Close(int returnCode) => ReturnCode = returnCode;
}
