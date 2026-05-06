using System.Diagnostics;
using UniGetUI.PackageEngine.Enums;

namespace UniGetUI.PackageEngine.ManagerClasses.Classes;

public interface IManagerLogger
{
    public IReadOnlyList<ITaskLogger> Operations { get; }
    INativeTaskLogger CreateNew(LoggableTaskType type);
    IProcessTaskLogger CreateNew(LoggableTaskType type, Process process);
}

public interface ITaskLogger
{
    IReadOnlyList<string> AsColoredString(bool verbose = false);
    void Close(int returnCode);
}

public interface INativeTaskLogger : ITaskLogger
{
    void Error(Exception? e);
    void Error(IReadOnlyList<string> lines);
    void Error(string? line);
    void Log(IReadOnlyList<string> lines);
    void Log(string? line);
}

public interface IProcessTaskLogger : ITaskLogger
{
    void AddToStdErr(IReadOnlyList<string> lines);
    void AddToStdErr(string? line);
    void AddToStdIn(IReadOnlyList<string> lines);
    void AddToStdIn(string? line);
    void AddToStdOut(IReadOnlyList<string> lines);
    void AddToStdOut(string? line);
}
