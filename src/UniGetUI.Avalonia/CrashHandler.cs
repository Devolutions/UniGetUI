using System.Diagnostics;
using System.Text;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia;

public static class CrashHandler
{
    public static readonly string PendingCrashFile =
        Path.Combine(Path.GetTempPath(), "UniGetUI_pending_crash.txt");

    public static void ReportFatalException(Exception e)
    {
        Debugger.Break();

        string langName = "Unknown";
        try
        {
            langName = CoreTools.GetCurrentLocale();
        }
        catch { }

        static string GetExceptionData(Exception ex)
        {
            try
            {
                var b = new StringBuilder();
                foreach (var key in ex.Data.Keys)
                    b.AppendLine($"{key}: {ex.Data[key]}");
                string r = b.ToString();
                return r.Any() ? r : "No extra data was provided";
            }
            catch (Exception inner)
            {
                return $"Failed to get exception Data with exception {inner.Message}";
            }
        }

        string iReport;
        try
        {
            var integrityReport = IntegrityTester.CheckIntegrity(false);
            iReport = IntegrityTester.GetReadableReport(integrityReport);
        }
        catch (Exception ex)
        {
            iReport = "Failed to compute integrity report: " + ex.GetType() + ": " + ex.Message;
        }

        string errorString = $$"""
            Environment details:
                    OS version: {{Environment.OSVersion.VersionString}}
                    Language: {{langName}}
                    APP Version: {{CoreData.VersionName}}
                    APP Build number: {{CoreData.BuildNumber}}
                    Executable: {{Environment.ProcessPath}}
                    Command-line arguments: {{Environment.CommandLine}}

            Integrity report:
                {{iReport.Replace("\n", "\n    ")}}

            Exception type: {{e.GetType()?.Name}} ({{e.GetType()}})
                Crash HResult: 0x{{(uint)e.HResult:X}} ({{(uint)e.HResult}}, {{e.HResult}})
                Crash Message: {{e.Message}}

                Crash Data:
                    {{GetExceptionData(e).Replace("\n", "\n        ")}}

                Crash Trace:
                    {{e.StackTrace?.Replace("\n", "\n        ")}}
            """;

        try
        {
            int depth = 0;
            while (e.InnerException is not null)
            {
                depth++;
                e = e.InnerException;
                errorString +=
                    "\n\n\n\n"
                    + $$"""
                        ———————————————————————————————————————————————————————————
                        Inner exception details (depth level: {{depth}})
                            Crash HResult: 0x{{(uint)e.HResult:X}} ({{(uint)e.HResult}}, {{e.HResult}})
                            Crash Message: {{e.Message}}

                            Crash Data:
                                {{GetExceptionData(e).Replace("\n", "\n        ")}}

                            Crash Traceback:
                                {{e.StackTrace?.Replace("\n", "\n        ")}}
                        """;
            }

            if (depth == 0)
                errorString += "\n\n\nNo inner exceptions found";
        }
        catch { }

        Console.WriteLine(errorString);

        // Persist crash data so the next normal app launch can show the report.
        try
        {
            File.WriteAllText(PendingCrashFile, errorString, Encoding.UTF8);
        }
        catch
        {
            // If we can't write the file, nothing more we can do — just exit.
        }

        Environment.Exit(1);
    }
}
