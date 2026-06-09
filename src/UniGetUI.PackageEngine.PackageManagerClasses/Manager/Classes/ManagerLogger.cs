using System.Diagnostics;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.ManagerClasses.Classes
{
    public class ManagerLogger : IManagerLogger
    {
        private readonly IPackageManager Manager;

        private readonly List<TaskLogger> operations = [];
        public IReadOnlyList<ITaskLogger> Operations
        {
            get => (IReadOnlyList<ITaskLogger>)operations;
        }

        public ManagerLogger(IPackageManager manager)
        {
            Manager = manager;
        }

        public IProcessTaskLogger CreateNew(LoggableTaskType type, Process process)
        {
            if (process.StartInfo is null)
            {
                throw new InvalidOperationException(
                    "Given process instance did not have a valid StartInfo value"
                );
            }

            ProcessTaskLogger operation = new(
                Manager,
                type,
                process.StartInfo.FileName,
                process.StartInfo.Arguments
            );
            operations.Add(operation);
            return operation;
        }

        public INativeTaskLogger CreateNew(LoggableTaskType type)
        {
            NativeTaskLogger operation = new(Manager, type);
            operations.Add(operation);
            return operation;
        }
    }

    public abstract class TaskLogger : ITaskLogger
    {
        protected DateTime StartTime;
        protected DateTime? EndTime;
        protected bool isComplete;
        protected bool isOpen;
        protected IReadOnlyList<string>? CachedMessage;
        protected IReadOnlyList<string>? CachedVerboseMessage;

        private const int RETURNCODE_UNSET = -200;
        protected int ReturnCode = -200;

        public TaskLogger()
        {
            StartTime = DateTime.Now;
            isComplete = false;
            isOpen = true;
        }

        ~TaskLogger()
        {
            if (isOpen)
            {
                Close(RETURNCODE_UNSET);
            }
        }

        public void Close(int returnCode)
        {
            ReturnCode = returnCode;
            EndTime = DateTime.Now;
            isOpen = false;
            isComplete = true;
            CachedMessage = null;
            CachedVerboseMessage = null;
        }

        /// <summary>
        /// Returns the output with a preceding digit representing the color of the line:
        ///   0. White
        ///   1. Grey
        ///   2. Red
        ///   3. Blue
        ///   4. Green
        ///   5. Yellow
        /// </summary>
        public abstract IReadOnlyList<string> AsColoredString(bool verbose = false);
    }

    public class ProcessTaskLogger : TaskLogger, IProcessTaskLogger
    {
        private readonly IPackageManager Manager;
        private readonly LoggableTaskType Type;

        private readonly string Executable;
        private readonly string Arguments;
        private readonly List<string> StdIn = [];
        private readonly List<string> StdOut = [];
        private readonly List<string> StdErr = [];

        public ProcessTaskLogger(
            IPackageManager manager,
            LoggableTaskType type,
            string executable,
            string arguments
        )
        {
            Type = type;
            Manager = manager;
            Executable = executable;
            Arguments = arguments;
        }

        public void AddToStdIn(string? line)
        {
            if (line is not null)
            {
                AddToStdIn(line.Split('\n'));
            }
        }

        public void AddToStdIn(IReadOnlyList<string> lines)
        {
            if (!isOpen)
            {
                throw new InvalidOperationException(
                    "Attempted to write log into an already-closed ProcessTaskLogger"
                );
            }

            foreach (string line in lines)
            {
                if (line != "")
                {
                    StdIn.Add(line);
                }
            }
        }

        public void AddToStdOut(string? line)
        {
            if (line is not null)
            {
                AddToStdOut(line.Split('\n'));
            }
        }

        public void AddToStdOut(IReadOnlyList<string> lines)
        {
            if (!isOpen)
            {
                throw new InvalidOperationException(
                    "Attempted to write log into an already-closed ProcessTaskLogger"
                );
            }

            foreach (string line in lines)
            {
                if (line != "")
                {
                    StdOut.Add(line);
                }
            }
        }

        public void AddToStdErr(string? line)
        {
            if (line is not null)
            {
                AddToStdErr(line.Split('\n'));
            }
        }

        public void AddToStdErr(IReadOnlyList<string> lines)
        {
            if (!isOpen)
            {
                throw new InvalidOperationException(
                    "Attempted to write log into an already-closed ProcessTaskLogger"
                );
            }

            foreach (string line in lines)
            {
                if (line != "")
                {
                    StdErr.Add(line);
                }
            }
        }

        public override IReadOnlyList<string> AsColoredString(bool verbose = false)
        {
            if (!verbose && CachedMessage is not null && isComplete)
            {
                return CachedMessage;
            }

            if (verbose && CachedVerboseMessage is not null && isComplete)
            {
                return CachedVerboseMessage;
            }

            List<string> result =
            [
                $"0Logged subprocess-based task on manager {Manager.Name}. Task type is {Type}",
                $"0Subprocess executable: \"{Executable}\"",
                $"0Command-line arguments: \"{Arguments}\"",
                $"0Process start time: {StartTime}",
                EndTime is null
                    ? "2Process end time:   UNFINISHED"
                    : $"0Process end time:   {EndTime}",
            ];

            if (StdIn.Count > 0)
            {
                result.Add("0");
                result.Add("0-- Process STDIN");
                if (verbose)
                {
                    foreach (string line in StdIn)
                    {
                        result.Add("3  " + line);
                    }
                }
                else
                {
                    result.Add("1 ...");
                }
            }
            if (StdOut.Count > 0)
            {
                result.Add("0");
                result.Add("0-- Process STDOUT");
                if (verbose)
                {
                    foreach (string line in StdOut)
                    {
                        result.Add("1  " + line);
                    }
                }
                else
                {
                    result.Add("1 ...");
                }
            }
            if (StdErr.Count > 0)
            {
                result.Add("0");
                result.Add("0-- Process STDERR");
                foreach (string line in StdErr)
                {
                    result.Add("2  " + line);
                }
            }
            result.Add("0");
            if (!isComplete)
            {
                result.Add("5Return code: Process has not finished yet");
            }
            else if (ReturnCode == -200)
            {
                result.Add("5Return code: UNSPECIFIED");
            }
            else if (ReturnCode == 0)
            {
                result.Add("4Return code: SUCCESS (0)");
            }
            else
            {
                result.Add(
                    $"2Return code: FAILED (0x{(uint)ReturnCode:X}, {(uint)ReturnCode}, {ReturnCode})"
                );
            }

            result.Add("0");
            result.Add("0——————————————————————————————————————————");
            result.Add("0");

            for (int i = 0; i < result.Count; i++)
                result[i] = Logger.Redact(result[i]);

            if (verbose)
            {
                return CachedVerboseMessage = result;
            }

            return CachedMessage = result;
        }
    }

    public class NativeTaskLogger : TaskLogger, INativeTaskLogger
    {
        private readonly IPackageManager Manager;
        private readonly LoggableTaskType Type;

        private readonly List<string> Info = [];
        private readonly List<string> Errors = [];

        public NativeTaskLogger(IPackageManager manager, LoggableTaskType type)
        {
            Type = type;
            Manager = manager;
        }

        public void Log(IReadOnlyList<string> lines)
        {
            if (!isOpen)
            {
                throw new InvalidOperationException(
                    "Attempted to write log into an already-closed NativeTaskLogger"
                );
            }

            foreach (string line in lines)
            {
                if (line != "")
                {
                    Info.Add(line);
                }
            }
        }

        public void Log(string? line)
        {
            if (line is not null)
            {
                Log(line.Split('\n'));
            }
        }

        public void Error(IReadOnlyList<string> lines)
        {
            if (!isOpen)
            {
                throw new InvalidOperationException(
                    "Attempted to write log into an already-closed NativeTaskLogger"
                );
            }

            foreach (string line in lines)
            {
                if (line != "")
                {
                    Errors.Add(line);
                }
            }
        }

        public void Error(string? line)
        {
            if (line is not null)
            {
                Error(line.Split('\n'));
            }
        }

        public void Error(Exception? e)
        {
            if (e is not null)
            {
                Error(e.ToString().Split('\n'));
            }
        }

        public override IReadOnlyList<string> AsColoredString(bool verbose = false)
        {
            if (!verbose && CachedMessage is not null && isComplete)
            {
                return CachedMessage;
            }

            if (verbose && CachedVerboseMessage is not null && isComplete)
            {
                return CachedVerboseMessage;
            }

            List<string> result =
            [
                $"0Logged native task on manager {Manager.Name}. Task type is {Type}",
                $"0Process start time: {StartTime}",
                EndTime is null
                    ? "2Process end time:   UNFINISHED"
                    : $"0Process end time:   {EndTime}",
            ];

            if (Info.Count > 0)
            {
                result.Add("0");
                result.Add("0-- Task information");
                if (verbose)
                {
                    foreach (string line in Info)
                    {
                        result.Add("1  " + line);
                    }
                }
                else
                {
                    result.Add("1 ...");
                }
            }
            if (Errors.Count > 0)
            {
                result.Add("0");
                result.Add("0-- Task errors");
                foreach (string line in Errors)
                {
                    result.Add("2  " + line);
                }
            }
            result.Add("0");
            if (!isComplete)
            {
                result.Add("5The task has not finished yet");
            }
            else if (ReturnCode == -200)
            {
                result.Add("5The task did not report a finish status");
            }
            else if (ReturnCode == 0)
            {
                result.Add("4The task reported success");
            }
            else
            {
                result.Add(
                    $"2The task reported a failure (0x{(uint)ReturnCode:X}, {(uint)ReturnCode}, {ReturnCode})"
                );
            }

            result.Add("0");
            result.Add("0——————————————————————————————————————————");
            result.Add("0");

            for (int i = 0; i < result.Count; i++)
                result[i] = Logger.Redact(result[i]);

            if (verbose)
            {
                return CachedVerboseMessage = result;
            }

            return CachedMessage = result;
        }
    }
}
