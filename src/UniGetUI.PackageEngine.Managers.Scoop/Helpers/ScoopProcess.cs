using System.Diagnostics;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.PackageEngine.Managers.ScoopManager
{
    internal static class ScoopProcess
    {
        public static IReadOnlyList<string> ReadLines(Process p, IProcessTaskLogger logger)
        {
            Task<string> stdErr = ReadStdErrAsync(p);

            List<string> lines = [];
            string? line;
            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                logger.AddToStdOut(line);
                lines.Add(line);
            }

            Finish(p, logger, stdErr);
            return lines;
        }

        public static string ReadToEnd(Process p, IProcessTaskLogger logger)
        {
            Task<string> stdErr = ReadStdErrAsync(p);

            string stdOut = p.StandardOutput.ReadToEnd();
            logger.AddToStdOut(stdOut);

            Finish(p, logger, stdErr);
            return stdOut;
        }

        public static Task<string> ReadStdErrAsync(Process p) =>
            Task.Run(p.StandardError.ReadToEnd);

        public static Task<string> ReadStdOutAsync(Process p) =>
            Task.Run(p.StandardOutput.ReadToEnd);

        public static string ReadWithTimeout(Task<string> read, int millisecondsTimeout)
        {
            try
            {
                return read.Wait(millisecondsTimeout) ? read.Result.Trim() : "";
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not read the output of a Scoop process: {ex.Message}");
                return "";
            }
        }

        private static void Finish(Process p, IProcessTaskLogger logger, Task<string> stdErr)
        {
            logger.AddToStdErr(stdErr.GetAwaiter().GetResult());
            p.WaitForExit();
            logger.Close(p.ExitCode);
        }
    }
}
