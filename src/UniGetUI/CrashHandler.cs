using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;

namespace UniGetUI;

public static class CrashHandler
{
    public static readonly string PendingCrashFile =
        Path.Combine(Path.GetTempPath(), "UniGetUI_pending_crash.txt");

    private const uint MB_ICONSTOP = 0x00000010;
    private const uint MB_OKCANCEL = 0x00000001;
    private const uint MB_YESNOCANCEL = 0x00000003;
    private const int IDOK = 1;
    private const int IDYES = 6;
    private const int IDNO = 7;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    // ── Missing-files handler ─────────────────────────────────────────────────
    private static void _reportMissingFiles(out bool showDetailedReport)
    {
        try
        {
            string installerPath = Path.Join(
                CoreData.UniGetUIExecutableDirectory,
                "UniGetUI.Installer.exe"
            );
            bool canAutoRepair = File.Exists(installerPath);

            var title = "UniGetUI - Missing Files";

            if (canAutoRepair)
            {
                var errorMessage =
                    "UniGetUI has detected that some required files are missing."
                    + "\n\nThis might be caused by an incomplete installation or corrupted files. Please reinstall UniGetUI."
                    + "\n\nPress YES to reinstall UniGetUI right now."
                    + "\nPress NO to close this prompt."
                    + "\nPress CANCEL to get more details about the crash.";

                var msgboxResult = MessageBox(
                    IntPtr.Zero,
                    errorMessage,
                    title,
                    MB_ICONSTOP | MB_YESNOCANCEL
                );
                if (msgboxResult is IDYES)
                {
                    Process.Start(installerPath, "/silent /NoDeployInstaller");
                }

                if (msgboxResult is IDYES or IDNO)
                    showDetailedReport = false;
                else
                    showDetailedReport = true; // msgboxResult is IDCANCEL
            }
            else
            {
                var errorMessage =
                    "UniGetUI has detected that some required files are missing."
                    + "\n\nThis might be caused by an incomplete installation or corrupted files. Please reinstall UniGetUI."
                    + "\n\nPress OK to close this prompt."
                    + "\nPress CANCEL to get more details about the crash.";

                var msgboxResult = MessageBox(
                    IntPtr.Zero,
                    errorMessage,
                    title,
                    MB_ICONSTOP | MB_OKCANCEL
                );
                if (msgboxResult is IDOK)
                    showDetailedReport = false;
                else
                    showDetailedReport = true; // msgboxResult is IDCANCEL
            }
        }
        catch
        {
            showDetailedReport = false;
        }
    }

    public static void ReportFatalException(Exception e)
    {
        Debugger.Break();

        if (!Environment.GetCommandLineArgs().Contains(CLIHandler.NO_CORRUPT_DIALOG))
        {
            Exception? fileEx = e;
            while (fileEx is not null)
            {
                if ((uint)fileEx.HResult is 0x80070002 or 0x8007007E or 0x802B000A)
                {
                    _reportMissingFiles(out bool showDetailedReport);
                    if (!showDetailedReport)
                    {
                        Environment.Exit(1);
                    }
                }
                fileEx = fileEx.InnerException;
            }
        }

        string LangName = "Unknown";
        try
        {
            LangName = CoreTools.GetCurrentLocale();
        }
        catch
        {
            // ignored
        }

        static string GetExceptionData(Exception e)
        {
            try
            {
                StringBuilder b = new();
                foreach (var key in e.Data.Keys)
                {
                    b.AppendLine($"{key}: {e.Data[key]}");
                }

                string r = b.ToString();
                return r.Any() ? r : "No extra data was provided";
            }
            catch (Exception ex)
            {
                return $"Failed to get exception Data with exception {ex.Message}";
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
            iReport = "Failed to compute integrity report: ";
            iReport += ex.GetType() + ": " + ex.Message;
        }

        string Error_String = $$"""
            Environment details:
                    Windows version: {{Environment.OSVersion.VersionString}}
                    Language: {{LangName}}
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
            int i = 0;
            while (e.InnerException is not null)
            {
                i++;
                e = e.InnerException;
                Error_String +=
                    "\n\n\n\n"
                    + $$"""
                        ———————————————————————————————————————————————————————————
                        Inner exception details (depth level: {{i}})
                            Crash HResult: 0x{{(uint)e.HResult:X}} ({{(uint)
                            e.HResult}}, {{e.HResult}})
                            Crash Message: {{e.Message}}

                            Crash Data:
                                {{GetExceptionData(e).Replace("\n", "\n        ")}}

                            Crash Traceback:
                                {{e.StackTrace?.Replace("\n", "\n        ")}}
                        """;
            }

            if (i == 0)
            {
                Error_String += $"\n\n\nNo inner exceptions found";
            }
        }
        catch
        {
            // ignore
        }

        Console.WriteLine(Error_String);

        // Persist crash data so the next normal app launch can show the report.
        try
        {
            File.WriteAllText(PendingCrashFile, Error_String, Encoding.UTF8);
        }
        catch
        {
            // If we can't write the file, nothing more we can do — just exit.
        }

        Environment.Exit(1);
    }
}
