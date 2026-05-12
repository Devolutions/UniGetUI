using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using UniGetUI.Core.Classes;
using UniGetUI.Core.Data;
using UniGetUI.Core.Language;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Enums;
#if WINDOWS
using Windows.Networking.Connectivity;
#endif

namespace UniGetUI.Core.Tools
{
    public static class CoreTools
    {
        public static HttpClientHandler GenericHttpClientParameters
        {
            get
            {
                Uri? proxyUri = null;
                IWebProxy? proxy = null;
                ICredentials? creds = null;

                if (Settings.Get(Settings.K.EnableProxy))
                    proxyUri = Settings.GetProxyUrl();
                if (Settings.Get(Settings.K.EnableProxyAuth))
                    creds = Settings.GetProxyCredentials();
                if (proxyUri is not null)
                    proxy = new WebProxy() { Address = proxyUri, Credentials = creds };

                return new()
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    AllowAutoRedirect = true,
                    UseProxy = (proxy is not null),
                    Proxy = proxy,
                };
            }
        }

        private static LanguageEngine LanguageEngine = new();

        static CoreTools()
        {
            LoadStaticTranslation();
        }

        /// <summary>
        /// Translate a string to the current language
        /// </summary>
        /// <param name="text">The string to translate</param>
        /// <returns>The translated string if available, the original string otherwise</returns>
        public static string Translate(string text)
        {
            return LanguageEngine.Translate(text);
        }

        public static string Translate(string text, Dictionary<string, object?> dict)
        {
            return LanguageEngine.Translate(text, dict);
        }

        public static string Translate(string text, params object[] values)
        {
            Dictionary<string, object?> dict = [];
            foreach ((object item, int index) in values.Select((item, index) => (item, index)))
            {
                dict.Add(index.ToString(), item);
            }

            return Translate(text, dict);
        }

        public static void ReloadLanguageEngineInstance(string ForceLanguage = "")
        {
            LanguageEngine = new LanguageEngine(ForceLanguage);
            LoadStaticTranslation();
        }

        private static void LoadStaticTranslation()
        {
            string localScopeName = CoreTools.Translate("User | Local");
            string globalScopeName = CoreTools.Translate("Machine | Global");

            CommonTranslations.ScopeNames[PackageScope.Local] = localScopeName;
            CommonTranslations.ScopeNames[PackageScope.Global] = globalScopeName;

            CommonTranslations.InvertedScopeNames.Clear();
            CommonTranslations.InvertedScopeNames.Add(globalScopeName, PackageScope.Global);
            CommonTranslations.InvertedScopeNames.Add(localScopeName, PackageScope.Local);
        }

        /// <summary>
        /// Dummy function to capture the strings that need to be translated but the translation is handled by a custom widget
        /// </summary>
        public static string AutoTranslated(string text)
        {
            return text;
        }

        /// <summary>
        /// Launches the self executable on a new process and kills the current process
        /// </summary>
        public static void RelaunchProcess()
        {
            Logger.Debug("Launching process: " + CoreData.UniGetUIExecutableFile);
            ScheduleRelaunchAfterExit();
            Logger.Warn("About to kill process");
            Environment.Exit(0);
        }

        public static void ScheduleRelaunchAfterExit(string? executablePath = null)
        {
            executablePath ??= CoreData.UniGetUIExecutableFile;
            int currentProcessId = Environment.ProcessId;
            string escapedExecutablePath = executablePath.Replace("'", "''");
            string command =
                $"Wait-Process -Id {currentProcessId}; Start-Process -FilePath '{escapedExecutablePath}'";

            Process.Start(
                new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -WindowStyle Hidden -Command \"{command}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            );
        }

        /// <summary>
        /// Finds an executable in path and returns its location
        /// </summary>
        /// <param name="command">The executable alias to find</param>
        /// <returns>A tuple containing: a boolean that represents whether the path was found or not; the path to the file if found.</returns>
        public static async Task<Tuple<bool, string>> WhichAsync(string command)
        {
            return await Task.Run(() => Which(command));
        }

        public static List<string> WhichMultiple(string command, bool updateEnv = true)
        {
            command = command.Replace(";", "").Replace("&", "").Trim();
            Logger.Debug($"Begin \"which\" search for command {command}");

            _ = updateEnv;

            if (string.IsNullOrWhiteSpace(command))
            {
                Logger.ImportantInfo($"Command {command} was not found on the system");
                return [];
            }

            string pathValue = GetSearchPath();

            List<string> lines = FindExecutableMatches(command, pathValue);
            if (lines.Count is 0)
            {
                Logger.ImportantInfo($"Command {command} was not found on the system");
                return [];
            }

            Logger.Debug(
                $"Command {command} was found on {lines[0]} (with {lines.Count - 1} more occurrences)"
            );
            return lines;
        }

        public static Tuple<bool, string> Which(string command, bool updateEnv = true)
        {
            var paths = WhichMultiple(command, updateEnv);
            return new(paths.Any(), paths.Any() ? paths[0] : "");
        }

        /// <summary>
        /// Formats a given package id as a name, capitalizing words and replacing separators with spaces
        /// </summary>
        /// <param name="name">A string containing the Id of a package</param>
        /// <returns>The formatted string</returns>
        public static string FormatAsName(string name)
        {
            name = name.Replace(".install", "")
                .Replace(".portable", "")
                .Replace("-", " ")
                .Replace("_", " ")
                .Split("/")[^1]
                .Split(":")[0];
            string newName = "";
            for (int i = 0; i < name.Length; i++)
            {
                if (
                    i == 0
                    || name[i - 1] == ' '
                    || name[i - 1] == '[' /* for vcpkg options */
                )
                {
                    newName += name[i].ToString().ToUpper();
                }
                else
                {
                    newName += name[i];
                }
            }

            newName = newName.Replace(" [", "[").Replace("[", " [");
            return newName;
        }

        /// <summary>
        /// Generates a random string composed of alphanumeric characters and numbers
        /// </summary>
        /// <param name="length">The length of the string</param>
        /// <returns>A string</returns>
        public static string RandomString(int length)
        {
            Random random = new();
            const string pool = "abcdefghijklmnopqrstuvwxyz0123456789";
            IEnumerable<char> chars = Enumerable
                .Range(0, length)
                .Select(_ => pool[random.Next(0, pool.Length)]);
            return new string(chars.ToArray());
        }

        /// <summary>
        /// Launches a .bat or .cmd file for the given filename
        /// </summary>
        /// <param name="path">The path of the batch file</param>
        /// <param name="WindowTitle">The title of the window</param>
        /// <param name="RunAsAdmin">Whether the batch file should be launched elevated or not</param>
        public static async Task LaunchBatchFile(
            string path,
            string WindowTitle = "",
            bool RunAsAdmin = false
        )
        {
            try
            {
                using Process p = new();
                if (OperatingSystem.IsWindows())
                {
                    p.StartInfo.FileName = "cmd.exe";
                    p.StartInfo.Arguments = "/C start \"" + WindowTitle + "\" \"" + path + "\"";
                    p.StartInfo.UseShellExecute = true;
                    p.StartInfo.Verb = RunAsAdmin ? "runas" : "";
                }
                else
                {
                    p.StartInfo.FileName = "/bin/sh";
                    p.StartInfo.ArgumentList.Add(path);
                    p.StartInfo.UseShellExecute = false;
                }

                p.StartInfo.CreateNoWindow = true;
                p.Start();
                await p.WaitForExitAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        /// <summary>
        /// Checks whether the current process has administrator privileges
        /// </summary>
        /// <returns>True if the process has administrator privileges</returns>
        public static bool IsAdministrator()
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    return string.Equals(Environment.UserName, "root", StringComparison.Ordinal);
                }

                return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(
                    WindowsBuiltInRole.Administrator
                );
            }
            catch (Exception e)
            {
                Logger.Warn("Could not check if user is administrator");
                Logger.Warn(e);
                return false;
            }
        }

        /// <summary>
        /// Checks whether the current process is running inside an MSIX package (Store / sideloaded).
        /// </summary>
        /// <returns>True when packaged, false when running unpackaged or on a non-Windows platform.</returns>
        public static bool IsPackagedApp()
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                int len = 0;
                int rc = GetCurrentPackageFullName(ref len, IntPtr.Zero);
                return rc != APPMODEL_ERROR_NO_PACKAGE;
            }
            catch
            {
                return false;
            }
        }

        private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

        [DllImport("kernel32.dll", SetLastError = false)]
        private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, IntPtr packageFullName);

        public static Task<long> GetFileSizeAsLongAsync(Uri? url) =>
            Task.Run(() => GetFileSizeAsLong(url));

        public static long GetFileSizeAsLong(Uri? url)
        {
            if (url is null)
                return 0;

            try
            {
                using HttpClient client = new(CoreTools.GenericHttpClientParameters);
                HttpResponseMessage response = client.Send(
                    new HttpRequestMessage(HttpMethod.Head, url)
                );
                return response.Content.Headers.ContentLength ?? 0;
            }
            catch (Exception e)
            {
                Logger.Warn($"Could not load file size for url={url}");
                Logger.Warn(e);
            }

            return 0;
        }

        public static string GetFileName(Uri url)
        {
            try
            {
                var handler = CoreTools.GenericHttpClientParameters;
                handler.AllowAutoRedirect = false;
                using HttpClient client = new(handler);
                HttpResponseMessage response = client.Send(
                    new HttpRequestMessage(HttpMethod.Head, url)
                );

                if (
                    response.StatusCode
                    is HttpStatusCode.Moved
                    or HttpStatusCode.Redirect
                    or HttpStatusCode.RedirectMethod
                    or HttpStatusCode.TemporaryRedirect
                    or HttpStatusCode.PermanentRedirect
                )
                {
                    return GetFileName(
                        response.Headers.Location
                        ?? throw new HttpRequestException(
                            "A redirect code was returned but no new location was given"
                        )
                    );
                }

                return response.Content.Headers.ContentDisposition?.FileName
                       ?? Path.GetFileName(url.LocalPath);
            }
            catch (Exception e)
            {
                Logger.Warn($"Failed to retrieve file name for URL: {url}");
                Logger.Warn(e);
                return string.Empty;
            }
        }

        public static Task<string> GetFileNameAsync(Uri url) => Task.Run(() => GetFileName(url));

        public struct Version : IComparable
        {
            public static readonly Version Null = new(-1, -1, -1, -1);

            public readonly int Major;
            public readonly int Minor;
            public readonly int Patch;
            public readonly int Remainder;

            public Version(int major, int minor = 0, int patch = 0, int remainder = 0)
            {
                Major = major;
                Minor = minor;
                Patch = patch;
                Remainder = remainder;
            }

            public int CompareTo(object? other_)
            {
                if (other_ is not Version other)
                    return 0;

                int major = Major.CompareTo(other.Major);
                if (major != 0)
                    return major;

                int minor = Minor.CompareTo(other.Minor);
                if (minor != 0)
                    return minor;

                int patch = Patch.CompareTo(other.Patch);
                if (patch != 0)
                    return patch;

                return Remainder.CompareTo(other.Remainder);
            }

            public static bool operator ==(Version left, Version right) =>
                left.CompareTo(right) == 0;

            public static bool operator !=(Version left, Version right) =>
                left.CompareTo(right) != 0;

            public static bool operator >=(Version left, Version right) =>
                left.CompareTo(right) >= 0;

            public static bool operator <=(Version left, Version right) =>
                left.CompareTo(right) <= 0;

            public static bool operator >(Version left, Version right) => left.CompareTo(right) > 0;

            public static bool operator <(Version left, Version right) => left.CompareTo(right) < 0;

            public bool Equals(Version other) =>
                Major == other.Major
                && Minor == other.Minor
                && Patch == other.Patch
                && Remainder == other.Remainder;

            public override bool Equals(object? obj) => obj is Version other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Remainder);
        }

        /// <summary>
        /// Converts a string into a double floating-point number.
        /// </summary>
        /// <param name="version">Any string</param>
        /// <returns>The best approximation of the string as a Version</returns>
        public static Version VersionStringToStruct(string version)
        {
            try
            {
                char[] separators = ['.', '-', '/', '#'];
                string[] versionItems = ["", "", "", ""];

                string[] segments = version.Split(separators, StringSplitOptions.RemoveEmptyEntries);
                foreach (var segment in segments)
                {
                    bool seenDigit = false;
                    bool seenLetterAfterDigit = false;
                    bool seenDigitAfterLetter = false;
                    foreach (char c in segment)
                    {
                        if (char.IsDigit(c))
                        {
                            if (seenLetterAfterDigit)
                                seenDigitAfterLetter = true;
                            seenDigit = true;
                        }
                        else if (char.IsLetter(c) && seenDigit)
                        {
                            seenLetterAfterDigit = true;
                        }
                    }

                    if (seenDigit && seenLetterAfterDigit && seenDigitAfterLetter)
                    {
                        Logger.Warn(
                            $"Version string {version} appears to contain non-numeric characters within a numeric segment and will be treated as unknown");
                        return CoreTools.Version.Null;
                    }
                }

                int dotCount = 0;
                bool first = true;

                foreach (char c in version)
                {
                    if (char.IsDigit(c))
                        versionItems[dotCount] += c;
                    else if (!first && separators.Contains(c))
                        if (dotCount < 3)
                            dotCount++;
                    first = false;
                }

                int[] numbers = { 0, 0, 0, 0 };
                for (int i = 0; i < 4; i++)
                {
                    if (int.TryParse(versionItems[i], out int val))
                        numbers[i] = val;
                }

                var ver = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
                return ver;
            }
            catch
            {
                Logger.Warn($"Failed to parse version {version} to float");
                return CoreTools.Version.Null;
            }
        }

        public static System.Version NormalizeVersionForComparison(System.Version version)
        {
            return version.Revision >= 0
                ? new System.Version(version.Major, version.Minor, version.Build)
                : version;
        }

        /// <summary>
        /// Returns the query that can be safely passed as a command-line parameter
        /// </summary>
        /// <param name="query">The query to make safe</param>
        /// <returns>The safe version of the query</returns>
        public static string EnsureSafeQueryString(string query)
        {
            return query
                .Replace(";", string.Empty)
                .Replace("&", string.Empty)
                .Replace("|", string.Empty)
                .Replace(">", string.Empty)
                .Replace("<", string.Empty)
                .Replace("%", string.Empty)
                .Replace("\"", string.Empty)
                .Replace("~", string.Empty)
                .Replace("?", string.Empty)
                .Replace("/", string.Empty)
                .Replace("'", string.Empty)
                .Replace("\\", string.Empty)
                .Replace("`", string.Empty);
        }

        /// <summary>
        /// Returns null if the string is empty
        /// </summary>
        /// <param name="value">The string to check</param>
        /// <returns>a string? instance</returns>
        public static string? GetStringOrNull(string? value)
        {
            if (value == "")
            {
                return null;
            }

            return value;
        }

        /// <summary>
        /// Returns a new Uri if the string is not empty. Returns null otherwise
        /// </summary>
        /// <param name="url">The null, empty or valid string</param>
        /// <returns>an Uri? instance</returns>
        public static Uri? GetUriOrNull(string? url)
        {
            if (url is "" or null)
            {
                return null;
            }

            return new Uri(url);
        }

        /// <summary>
        /// Enables GSudo cache for the current process
        /// </summary>
        private static bool _isCaching;

        public static async Task CacheUACForCurrentProcess()
        {
            if (Settings.Get(Settings.K.ProhibitElevation))
            {
                Logger.Error(
                    "Elevation is prohibited, CacheUACForCurrentProcess() call will be ignored"
                );
                return;
            }

            while (_isCaching)
                await Task.Delay(100);

            try
            {
                _isCaching = true;
                Logger.Info("Caching admin rights for process id " + Environment.ProcessId);

                var elevatorName = Path.GetFileName(CoreData.ElevatorPath);

                // pkexec prompts on every invocation and has no caching protocol.
                if (elevatorName == "pkexec")
                {
                    _isCaching = false;
                    return;
                }

                // sudo: -v validates/extends the cached timestamp.
                // Prepend -A only when the SUDO_ASKPASS helper is configured.
                // gsudo / UniGetUI Elevator.exe: use the gsudo cache protocol.
                string cacheArgs = elevatorName == "sudo"
                    ? (CoreData.ElevatorArgs.Contains("-A") ? "-Av" : "-v")
                    : "cache on --pid " + Environment.ProcessId + " -d 1";

                using Process p = new()
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = CoreData.ElevatorPath,
                        Arguments = cacheArgs,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                    },
                };
                p.Start();
                await p.WaitForExitAsync();
                _isCaching = false;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                _isCaching = false;
            }
        }

        /// <summary>
        /// Reset UAC cache for the current process
        /// </summary>
        public static async Task ResetUACForCurrentProcess()
        {
            if (Settings.Get(Settings.K.ProhibitElevation))
            {
                Logger.Error(
                    "Elevation is prohibited, ResetUACForCurrentProcess() call will be ignored"
                );
                return;
            }

            Logger.Info(
                "Resetting administrator rights cache for process id " + Environment.ProcessId
            );

            var elevatorName = Path.GetFileName(CoreData.ElevatorPath);

            // pkexec prompts on every invocation and has no caching protocol.
            if (elevatorName == "pkexec")
            {
                return;
            }

            // sudo: -K removes all cached timestamps.
            // gsudo / UniGetUI Elevator.exe: use the gsudo cache protocol.
            string resetArgs = elevatorName == "sudo"
                ? "-K"
                : "cache off --pid " + Environment.ProcessId;

            using Process p = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = CoreData.ElevatorPath,
                    Arguments = resetArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                },
            };
            p.Start();
            await p.WaitForExitAsync();
        }

        /// <summary>
        /// Returns the hash of the given string in the form of a long integer.
        /// The long integer is built with the first half of the MD5 sum of the given string
        /// </summary>
        /// <param name="inputString">A non-empty string</param>
        /// <returns>A long integer containing the first half of the bytes resulting from MD5 summing inputString</returns>
        public static long HashStringAsLong(string inputString)
        {
            byte[] bytes = MD5.HashData(Encoding.UTF8.GetBytes(inputString));
            return BitConverter.ToInt64(bytes, 0);
        }

        /// <summary>
        /// Creates a symbolic link between directories
        /// </summary>
        /// <param name="linkPath">The location of the link to be created</param>
        /// <param name="targetPath">The location of the real folder where to point</param>
        public static void CreateSymbolicLinkDir(string linkPath, string targetPath)
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);

            if (!Directory.Exists(linkPath))
            {
                throw new InvalidOperationException(
                    $"The symbolic link '{linkPath}' was not created successfully."
                );
            }
        }

        /// <summary>
        /// Will check whether the given folder is a symbolic link
        /// </summary>
        /// <param name="path">The folder to check</param>
        /// <exception cref="FileNotFoundException"></exception>
        public static bool IsSymbolicLinkDir(string path)
        {
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                throw new FileNotFoundException("The specified path does not exist.", path);
            }

            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }

        /// <summary>
        /// Returns the updated environment variables on a new ProcessStartInfo object
        /// </summary>
        /// <returns></returns>
        public static ProcessStartInfo UpdateEnvironmentVariables()
        {
            return UpdateEnvironmentVariables(new ProcessStartInfo());
        }

        /// <summary>
        /// Returns the updated environment variables on the returned ProcessStartInfo object
        /// </summary>
        /// <returns></returns>
        public static ProcessStartInfo UpdateEnvironmentVariables(ProcessStartInfo info)
        {
            if (!OperatingSystem.IsWindows())
            {
                foreach (DictionaryEntry env in Environment.GetEnvironmentVariables())
                {
                    info.Environment[env.Key?.ToString() ?? "UNKNOWN"] = env.Value?.ToString();
                }

                return info;
            }

            foreach (
                DictionaryEntry env in Environment.GetEnvironmentVariables(
                    EnvironmentVariableTarget.Machine
                )
            )
            {
                info.Environment[env.Key?.ToString() ?? "UNKNOWN"] = env.Value?.ToString();
            }

            foreach (
                DictionaryEntry env in Environment.GetEnvironmentVariables(
                    EnvironmentVariableTarget.User
                )
            )
            {
                string key = env.Key.ToString() ?? "";
                string newValue = env.Value?.ToString() ?? "";
                if (
                    info.Environment.TryGetValue(key, out string? oldValue)
                    && oldValue is not null
                    && oldValue.Contains(';')
                    && newValue != ""
                )
                {
                    info.Environment[key] = oldValue + ";" + newValue;
                }
                else
                {
                    info.Environment[key] = newValue;
                }
            }

            return info;
        }

        /// <summary>
        /// Pings the update server and 3 well-known sites to check for internet availability
        /// </summary>
        public static async Task WaitForInternetConnection() =>
            await TaskRecycler<int>.RunOrAttachAsync_VOID(_waitForInternetConnection);

        public static void _waitForInternetConnection()
        {
            if (Settings.Get(Settings.K.DisableWaitForInternetConnection))
                return;

            Logger.Debug("Checking for internet connectivity...");
            bool internetLost = false;

#if WINDOWS
            var profile = NetworkInformation.GetInternetConnectionProfile();
            while (
                profile is null
                || profile.GetNetworkConnectivityLevel()
                    is not NetworkConnectivityLevel.InternetAccess
            )
            {
                Thread.Sleep(1000);
                profile = NetworkInformation.GetInternetConnectionProfile();
                if (!internetLost)
                {
                    Logger.Warn(
                        "User is not connected to the internet, waiting for an internet connectio to be available..."
                    );
                    internetLost = true;
                }
            }
#else
            while (!NetworkInterface.GetIsNetworkAvailable())
            {
                Thread.Sleep(1000);
                if (!internetLost)
                {
                    Logger.Warn(
                        "User is not connected to the internet, waiting for an internet connection to be available..."
                    );
                    internetLost = true;
                }
            }
#endif
            Logger.Debug("Internet connectivity was established.");
        }

        public static string TextProgressGenerator(int length, int progressPercent, string? extra)
        {
            int done = (int)((progressPercent / 100.0) * (length));
            int rest = length - done;

            StringBuilder builder = new StringBuilder()
                .Append('[')
                .Append('#', done)
                .Append('.', rest)
                .Append("] ")
                .Append(progressPercent)
                .Append('%');

            if (extra is not null)
            {
                builder.Append(" (").Append(extra).Append(')');
            }

            return builder.ToString();
        }

        public static string FormatAsSize(long number, int decimals = 1)
        {
            const double KiloByte = 1024d;
            const double MegaByte = 1024d * 1024d;
            const double GigaByte = 1024d * 1024d * 1024d;
            const double TeraByte = 1024d * 1024d * 1024d * 1024d;

            if (number >= TeraByte)
            {
                return $"{(number / TeraByte).ToString($"F{decimals}")} TB";
            }

            if (number >= GigaByte)
            {
                return $"{(number / GigaByte).ToString($"F{decimals}")} GB";
            }

            if (number >= MegaByte)
            {
                return $"{(number / MegaByte).ToString($"F{decimals}")} MB";
            }

            if (number >= KiloByte)
            {
                return $"{(number / KiloByte).ToString($"F{decimals}")} KB";
            }

            return $"{number} Bytes";
        }

        public static async Task ShowFileOnExplorer(string path)
        {
            try
            {
                if (!File.Exists(path))
                    throw new FileNotFoundException($"The file {path} was not found");

                if (!OperatingSystem.IsWindows())
                {
                    Launch(Path.GetDirectoryName(path));
                    return;
                }

                Process p = new()
                {
                    StartInfo = new()
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select, \"{path}\"",
                        UseShellExecute = true,
                        CreateNoWindow = true,
                    },
                };
                p.Start();
                await p.WaitForExitAsync();
                p.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        public static void Launch(string? path)
        {
            try
            {
                if (path is null)
                    return;

                var p = new Process()
                {
                    StartInfo = new() { FileName = path, UseShellExecute = true, CreateNoWindow = true, },
                };
                p.Start();
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        public static string GetCurrentLocale()
        {
            return LanguageEngine?.Locale ?? "Unset/Unknown";
        }

        private static readonly HashSet<char> _illegalPathChars = Path.GetInvalidFileNameChars()
            .ToHashSet();

        public static string MakeValidFileName(string name) =>
            string.Concat(name.Where(x => !_illegalPathChars.Contains(x)));

        // Safely wait for a task that may throw an exception we don't care about
        public static async void FinalizeDangerousTask(Task t)
        {
            try
            {
                await t.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error($"Task {t} crashed with exception:");
                Logger.Error(ex);
            }
        }

        private static List<string> FindExecutableMatches(string command, string pathValue)
        {
            List<string> matches = [];
            HashSet<string> seen = new(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal
            );

            if (HasDirectoryComponent(command))
            {
                AddExecutableMatches(
                    matches,
                    seen,
                    Path.GetDirectoryName(command) ?? string.Empty,
                    Path.GetFileName(command)
                );
                return matches;
            }

            foreach (string directory in EnumerateSearchDirectories(pathValue))
            {
                AddExecutableMatches(matches, seen, directory, command);
            }

            return matches;
        }

        private static void AddExecutableMatches(
            List<string> matches,
            HashSet<string> seen,
            string directory,
            string command
        )
        {
            string searchDirectory = NormalizeSearchDirectory(directory);
            if (searchDirectory.Length is 0)
            {
                return;
            }

            foreach (string candidateName in EnumerateCandidateFileNames(command))
            {
                TryAddExecutablePath(Path.Combine(searchDirectory, candidateName), matches, seen);
            }
        }

        private static IEnumerable<string> EnumerateSearchDirectories(string pathValue)
        {
            if (OperatingSystem.IsWindows())
            {
                yield return Environment.CurrentDirectory;
            }

            foreach (string pathEntry in pathValue.Split(Path.PathSeparator))
            {
                string normalizedEntry = NormalizeSearchDirectory(pathEntry);
                if (normalizedEntry.Length is not 0)
                {
                    yield return normalizedEntry;
                }
            }
        }

        private static string NormalizeSearchDirectory(string? pathEntry)
        {
            if (string.IsNullOrWhiteSpace(pathEntry))
            {
                return Environment.CurrentDirectory;
            }

            return pathEntry.Trim().Trim('"');
        }

        private static IEnumerable<string> EnumerateCandidateFileNames(string command)
        {
            if (!OperatingSystem.IsWindows() || Path.HasExtension(command))
            {
                yield return command;
                yield break;
            }

            foreach (string extension in GetWindowsExecutableExtensions())
            {
                yield return command + extension;
            }
        }

        private static IReadOnlyList<string> GetWindowsExecutableExtensions()
        {
            List<string> extensions = (
                    Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD"
                )
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(extension => extension.Trim())
                .Where(extension => !string.IsNullOrWhiteSpace(extension))
                .Select(extension => extension.StartsWith('.') ? extension : "." + extension)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return extensions.Count > 0 ? extensions : [".COM", ".EXE", ".BAT", ".CMD"];
        }

        private static void TryAddExecutablePath(
            string candidatePath,
            List<string> matches,
            HashSet<string> seen
        )
        {
            if (!File.Exists(candidatePath) || !IsExecutablePath(candidatePath))
            {
                return;
            }

            string fullPath = Path.GetFullPath(candidatePath);
            if (seen.Add(fullPath))
            {
                matches.Add(fullPath);
            }
        }

        private static bool IsExecutablePath(string candidatePath)
        {
            if (OperatingSystem.IsWindows())
            {
                return true;
            }

            try
            {
                const UnixFileMode ExecutableBits =
                    UnixFileMode.UserExecute
                    | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherExecute;

                return (File.GetUnixFileMode(candidatePath) & ExecutableBits) is not 0;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }

        private static bool HasDirectoryComponent(string command) =>
            Path.IsPathRooted(command)
            || command.Contains(Path.DirectorySeparatorChar)
            || command.Contains(Path.AltDirectorySeparatorChar);

        private static string GetSearchPath()
        {
            if (!OperatingSystem.IsWindows())
            {
                string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                if (IsRunningInWsl())
                {
                    currentPath = RemoveWindowsHostPathEntries(currentPath);
                }

                return currentPath;
            }

            string separator = Path.PathSeparator.ToString();
            string pathValue =
            (
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)
                ?? string.Empty
            ) + separator;
            pathValue +=
            (
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)
                ?? string.Empty
            ) + separator;
            pathValue +=
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process)
                ?? string.Empty;
            return pathValue.Replace(separator + separator, separator).Trim(Path.PathSeparator);
        }

        private static bool IsRunningInWsl()
        {
            if (!OperatingSystem.IsLinux())
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSL_INTEROP"));
        }

        private static string RemoveWindowsHostPathEntries(string pathValue)
        {
            if (string.IsNullOrWhiteSpace(pathValue))
            {
                return string.Empty;
            }

            string separator = Path.PathSeparator.ToString();
            IEnumerable<string> filteredEntries = pathValue
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Trim())
                .Where(entry => !IsWindowsHostMountPath(entry));

            return string.Join(separator, filteredEntries);
        }

        private static bool IsWindowsHostMountPath(string pathEntry)
        {
            if (!pathEntry.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (pathEntry.Length < 6)
            {
                return false;
            }

            char driveLetter = pathEntry[5];
            if (!char.IsAsciiLetter(driveLetter))
            {
                return false;
            }

            return pathEntry.Length == 6 || pathEntry[6] == '/';
        }
    }
}
