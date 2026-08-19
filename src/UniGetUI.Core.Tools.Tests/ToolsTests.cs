using System.Diagnostics;
using System.Runtime.InteropServices;
using UniGetUI.Core.Language;
using UniGetUI.PackageEngine.Enums;

namespace UniGetUI.Core.Tools.Tests
{
    public class ToolsTests
    {
        [Theory]
        [InlineData("NonExistentString", false)]
        [InlineData(" ", false)]
        [InlineData("", false)]
        [InlineData("@@", false)]
        [InlineData("No packages were found", true)]
        [InlineData("Add packages or open an existing package bundle", true)]
        public void TranslateFunctionTester(string textEntry, bool TranslationExists)
        {
            LanguageEngine langEngine = new("fr");
            CoreTools.ReloadLanguageEngineInstance("fr");

            Assert.Equal(CoreTools.Translate(textEntry), langEngine.Translate(textEntry));

            if (TranslationExists)
            {
                Assert.NotEqual(CoreTools.Translate(textEntry), textEntry);
            }
            else
            {
                Assert.Equal(CoreTools.Translate(textEntry), textEntry);
            }

            Assert.Equal(CoreTools.AutoTranslated(textEntry), textEntry);
        }

        [Fact]
        public void TestStaticallyLoadedLanguages()
        {
            CoreTools.ReloadLanguageEngineInstance("ca");

            Assert.Equal("Usuari | Local", CommonTranslations.ScopeNames[PackageScope.Local]);
            Assert.Equal("Màquina | Global", CommonTranslations.ScopeNames[PackageScope.Global]);

            Assert.Equal(
                PackageScope.Global,
                CommonTranslations.InvertedScopeNames["Màquina | Global"]
            );
            Assert.Equal(
                PackageScope.Local,
                CommonTranslations.InvertedScopeNames["Usuari | Local"]
            );
        }

        [Fact]
        public void EscapeCommandLineArgument_WrapsSimplePathInQuotes()
        {
            Assert.Equal("\"C:\\dev\\contoso\"", CoreTools.EscapeCommandLineArgument(@"C:\dev\contoso"));
            Assert.Equal("\"C:\\Program Files\\App\"", CoreTools.EscapeCommandLineArgument(@"C:\Program Files\App"));
        }

        [Fact]
        public void EscapeCommandLineArgument_EscapesEmbeddedQuoteToPreventInjection()
        {
            Assert.Equal("\"C:\\x\\\" --evil\"", CoreTools.EscapeCommandLineArgument("C:\\x\" --evil"));
        }

        [Fact]
        public void EscapeCommandLineArgument_DoublesTrailingBackslashes()
        {
            Assert.Equal("\"C:\\App\\\\\"", CoreTools.EscapeCommandLineArgument(@"C:\App\"));
        }

        [Theory]
        [InlineData(@"C:\dev\contoso")]
        [InlineData(@"C:\Program Files\App")]
        [InlineData(@"C:\App\")]
        [InlineData("C:\\x\" --disable-hash-check")]
        [InlineData(@"C:\weird path\with spaces\and\")]
        [InlineData("plain")]
        [InlineData("")]
        public void EscapeCommandLineArgument_RoundTripsThroughWindowsParser(string argument)
        {
            if (!OperatingSystem.IsWindows())
                return;

            string escaped = CoreTools.EscapeCommandLineArgument(argument);
            string[] parsed = SplitWindowsCommandLine("app.exe " + escaped);

            Assert.Equal(2, parsed.Length);
            Assert.Equal(argument, parsed[1]);
        }

        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private static string[] SplitWindowsCommandLine(string commandLine)
        {
            IntPtr argv = CommandLineToArgvW(commandLine, out int argc);
            if (argv == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            try
            {
                string[] result = new string[argc];
                for (int i = 0; i < argc; i++)
                {
                    IntPtr entry = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                    result[i] = Marshal.PtrToStringUni(entry) ?? "";
                }
                return result;
            }
            finally
            {
                LocalFree(argv);
            }
        }

        [Fact]
        public async Task TestWhichFunctionForExistingFile()
        {
            string existingCommand = OperatingSystem.IsWindows() ? "cmd.exe" : "sh";
            Tuple<bool, string> result = await CoreTools.WhichAsync(existingCommand);
            Assert.True(result.Item1);
            Assert.True(File.Exists(result.Item2));
        }

        [Fact]
        public async Task TestWhichFunctionForNonExistingFile()
        {
            Tuple<bool, string> result = await CoreTools.WhichAsync(
                "nonexistentfile-does-not-exist"
            );
            Assert.False(result.Item1);
            Assert.Equal("", result.Item2);
        }

        [Fact]
        public void TestWhichFunctionForDirectPath()
        {
            string tempDirectory = CreateTemporaryDirectory();
            string commandPath = Path.Combine(
                tempDirectory,
                OperatingSystem.IsWindows() ? "unigetui-direct-path-test.cmd" : "unigetui-direct-path-test"
            );

            try
            {
                CreateCommandFile(commandPath);

                Tuple<bool, string> result = CoreTools.Which(commandPath);

                Assert.True(result.Item1);
                Assert.Equal(commandPath, result.Item2);
            }
            finally
            {
                Directory.Delete(tempDirectory, true);
            }
        }

        [Fact]
        public void TestWhichMultipleRespectsProcessPathOrder()
        {
            string tempDirectory1 = CreateTemporaryDirectory();
            string tempDirectory2 = CreateTemporaryDirectory();
            string commandName = OperatingSystem.IsWindows()
                ? "unigetui-path-order-test.exe"
                : "unigetui-path-order-test";
            string commandPath1 = Path.Combine(tempDirectory1, commandName);
            string commandPath2 = Path.Combine(tempDirectory2, commandName);
            string? oldPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process);

            try
            {
                CreateCommandFile(commandPath1);
                CreateCommandFile(commandPath2);
                Environment.SetEnvironmentVariable(
                    "PATH",
                    string.Join(Path.PathSeparator, tempDirectory1, tempDirectory2),
                    EnvironmentVariableTarget.Process
                );

                List<string> result = CoreTools.WhichMultiple(commandName);

                Assert.Equal([commandPath1, commandPath2], result);
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    "PATH",
                    oldPath,
                    EnvironmentVariableTarget.Process
                );
                Directory.Delete(tempDirectory1, true);
                Directory.Delete(tempDirectory2, true);
            }
        }

        [Fact]
        public void TestWhichFunctionResolvesPathextOnWindows()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            string tempDirectory = CreateTemporaryDirectory();
            string commandPath = Path.Combine(tempDirectory, "unigetui-pathext-test.exe");
            string? oldPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process);
            string? oldPathExt = Environment.GetEnvironmentVariable(
                "PATHEXT",
                EnvironmentVariableTarget.Process
            );

            try
            {
                CreateCommandFile(commandPath);
                Environment.SetEnvironmentVariable(
                    "PATH",
                    tempDirectory,
                    EnvironmentVariableTarget.Process
                );
                Environment.SetEnvironmentVariable(
                    "PATHEXT",
                    ".EXE;.CMD",
                    EnvironmentVariableTarget.Process
                );

                Tuple<bool, string> result = CoreTools.Which("unigetui-pathext-test");

                Assert.True(result.Item1);
                Assert.Equal(commandPath, result.Item2, StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    "PATH",
                    oldPath,
                    EnvironmentVariableTarget.Process
                );
                Environment.SetEnvironmentVariable(
                    "PATHEXT",
                    oldPathExt,
                    EnvironmentVariableTarget.Process
                );
                Directory.Delete(tempDirectory, true);
            }
        }

        [Fact]
        public void TestWhichFunctionRequiresExecutableBitOnUnix()
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            string tempDirectory = CreateTemporaryDirectory();
            string commandName = "unigetui-unix-executable-bit-test";
            string commandPath = Path.Combine(tempDirectory, commandName);
            string? oldPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process);

            try
            {
                File.WriteAllText(commandPath, string.Empty);
                File.SetUnixFileMode(
                    commandPath,
                    UnixFileMode.UserRead
                        | UnixFileMode.UserWrite
                        | UnixFileMode.GroupRead
                        | UnixFileMode.OtherRead
                );
                Environment.SetEnvironmentVariable(
                    "PATH",
                    tempDirectory,
                    EnvironmentVariableTarget.Process
                );

                Tuple<bool, string> result = CoreTools.Which(commandName);

                Assert.False(result.Item1);
                Assert.Equal("", result.Item2);
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    "PATH",
                    oldPath,
                    EnvironmentVariableTarget.Process
                );
                Directory.Delete(tempDirectory, true);
            }
        }

        [Theory]
        [InlineData("7zip19.00-helpEr", "7zip19.00 HelpEr")]
        [InlineData("packagename", "Packagename")]
        [InlineData("Packagename", "Packagename")]
        [InlineData("PackageName", "PackageName")]
        [InlineData("pacKagENaMe", "PacKagENaMe")]
        [InlineData("PACKAGENAME", "PACKAGENAME")]
        [InlineData("package-Name", "Package Name")]
        [InlineData("pub-name/pkg-version_2.00.portable", "Pkg Version 2.00")]
        [InlineData("!helloWorld", "!helloWorld")]
        [InlineData("@stylistic/eslint-plugin", "Eslint Plugin")]
        [InlineData("Flask-RESTful", "Flask RESTful")]
        [InlineData("vcpkg-item[option]", "Vcpkg Item [Option]")]
        [InlineData("vcpkg-item[multi-option]", "Vcpkg Item [Multi Option]")]
        [InlineData("vcpkg-item[multi-option]:triplet", "Vcpkg Item [Multi Option]")]
        [InlineData("vcpkg-single-item:triplet", "Vcpkg Single Item")]
        public void TestFormatAsName(string id, string name)
        {
            Assert.Equal(name, CoreTools.FormatAsName(id));
        }

        [Theory]
        [InlineData(89)]
        [InlineData(33)]
        [InlineData(558)]
        [InlineData(12)]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(64)]
        public void TestRandomStringGenerator(int length)
        {
            string string1 = CoreTools.RandomString(length);
            string string2 = CoreTools.RandomString(length);
            string string3 = CoreTools.RandomString(length);
            Assert.Equal(string1.Length, length);
            Assert.Equal(string2.Length, length);
            Assert.Equal(string3.Length, length);

            // Zero-lenghted strings are always equal
            // One-lenghted strings are likely to be equal
            if (length > 1)
            {
                Assert.NotEqual(string1, string2);
                Assert.NotEqual(string2, string3);
                Assert.NotEqual(string1, string3);
            }

            foreach (string s in new[] { string1, string2, string3 })
            {
                foreach (char c in s)
                {
                    Assert.True("abcdefghijklmnopqrstuvwxyz0123456789".Contains(c));
                }
            }
        }

        [Theory]
        [InlineData("", 0)]
        [InlineData(
            "https://invalid.url.com/this/is/an/invalid.php?file=to_test&if=the&code_returns=zero",
            0
        )]
        [InlineData("https://raw.githubusercontent.com/Devolutions/UniGetUI/main/src/UniGetUI.Core.IconEngine.Tests/TestData/unigetui.png", 19788)]
        public async Task TestFileSizeLoader(string uri, long expectedSize)
        {
            long size = await CoreTools.GetFileSizeAsLongAsync(uri != "" ? new Uri(uri) : null);
            Assert.Equal(expectedSize, size);
        }

        [Theory]
        [InlineData("1000.0", 1000, 0, 0, 0)]
        [InlineData("2.4", 2, 4, 0, 0)]
        [InlineData("33a.12-beta5", 33, 12, 5, 0)]
        [InlineData("0", 0, 0, 0, 0)]
        [InlineData("", 0, 0, 0, 0)]
        [InlineData("dfgfdsgdfg", 0, 0, 0, 0)]
        [InlineData("-12", 12, 0, 0, 0)]
        // Segments past the fourth are kept separately instead of being appended to Remainder,
        // which used to turn "1" followed by "0" into 10 and "1" followed by "05" into 105.
        [InlineData("4.0.0.1.0", 4, 0, 0, 1)]
        [InlineData("4.0.0.1.05", 4, 0, 0, 1)]
        [InlineData("2024.30.04.1223", 2024, 30, 4, 1223)]
        [InlineData("0.0", 0, 0, 0, 0)]
        // Build revisions appended with '_' (Homebrew) must become their own segment, so that
        // 18.4_1 stays below 18.6 instead of being read as 18.41 (issue #5293).
        [InlineData("18.4_1", 18, 4, 1, 0)]
        [InlineData("3.14.3_1", 3, 14, 3, 1)]
        [InlineData("1_2_3_4", 1, 2, 3, 4)]
        public void TestGetVersionStringAsFloat(string version, int i1, int i2, int i3, int i4)
        {
            CoreTools.Version v = CoreTools.VersionStringToStruct(version);
            Assert.Equal(i1, v.Major);
            Assert.Equal(i2, v.Minor);
            Assert.Equal(i3, v.Patch);
            Assert.Equal(i4, v.Remainder);
        }

        [Fact]
        public void TestGetVersionStringAsFloat_WithNonNumericVersion_ReturnsNull()
        {
            CoreTools.Version v = CoreTools.VersionStringToStruct("10c8e557");
            Assert.Equal(CoreTools.Version.Null, v);
        }

        // An underscore introducing a pre-release tag rather than a numeric revision must keep
        // reporting as unknown, as it did before '_' became a segment separator. Parsing these
        // would rank the pre-release above the final release and hide the 2.0.0_rc1 -> 2.0.0
        // update, so the ambiguity check deliberately does not split on '_'.
        [Theory]
        [InlineData("2.0.0_rc1")]
        [InlineData("2.0.0_beta2")]
        [InlineData("1.0.0+build_1")]
        [InlineData("1.2_3a4")]
        public void TestGetVersionStringAsFloat_WithUnderscoredPreReleaseTag_ReturnsNull(string version)
        {
            Assert.Equal(CoreTools.Version.Null, CoreTools.VersionStringToStruct(version));
        }

        // ...while an underscore introducing a numeric revision must parse, since that is the
        // whole point of treating '_' as a separator.
        [Theory]
        [InlineData("18.4_1", 18, 4, 1, 0)]
        [InlineData("1.2.3_1", 1, 2, 3, 1)]
        public void TestGetVersionStringAsFloat_WithUnderscoredRevision_Parses(
            string version, int i1, int i2, int i3, int i4)
        {
            CoreTools.Version v = CoreTools.VersionStringToStruct(version);
            Assert.NotEqual(CoreTools.Version.Null, v);
            Assert.Equal(i1, v.Major);
            Assert.Equal(i2, v.Minor);
            Assert.Equal(i3, v.Patch);
            Assert.Equal(i4, v.Remainder);
        }

        // Known limitation, recorded rather than endorsed: a pre-release tag carrying no number is
        // dropped entirely, so the pre-release parses equal to its final release and
        // NewerVersionIsInstalled() hides the upgrade onto that release. Every separator behaves
        // this way, which is why '_' is not special-cased for it -- fixing this means ranking
        // pre-releases below their release for all managers, well beyond the scope of the '_' work.
        // Asserted against the '-' and '.' spellings so all variants move together when it is
        // fixed.
        [Theory]
        [InlineData("2.0.0_alpha", "2.0.0")]
        [InlineData("2.0.0-alpha", "2.0.0")]
        [InlineData("2.0.0.alpha", "2.0.0")]
        [InlineData("1.0.0_pre", "1.0.0")]
        public void TestPreReleaseTagWithoutNumberStillParsesEqualToItsRelease(
            string preRelease, string release)
        {
            Assert.Equal(
                CoreTools.VersionStringToStruct(release),
                CoreTools.VersionStringToStruct(preRelease)
            );
        }

        // Versions that only differ past the fourth segment must still order correctly. The
        // four-component assertions above cannot express this, since the difference lives in the
        // segments beyond Remainder.
        [Theory]
        // A build revision on a four-part version used to inflate Remainder (1.2.3.41), making
        // the installed version compare as greater than the newer release (issue #5293).
        [InlineData("1.2.3.4_1", "1.2.3.5")]
        [InlineData("1.2.3.4_1", "1.2.3.4_2")]
        [InlineData("3.1.4.1_1", "3.1.4.2")]
        [InlineData("4.0.0.1.0", "4.0.0.1.05")]
        [InlineData("4.0.0.1.5", "4.0.0.1.10")]
        public void TestVersionOrderingBeyondFourthSegment(string lower, string higher)
        {
            CoreTools.Version low = CoreTools.VersionStringToStruct(lower);
            CoreTools.Version high = CoreTools.VersionStringToStruct(higher);

            Assert.True(low < high, $"expected {lower} < {higher}");
            Assert.True(high > low, $"expected {higher} > {lower}");
            Assert.NotEqual(low, high);
        }

        [Theory]
        // Trailing zeroes past the fourth segment are trimmed, so these are the same version and
        // must agree on equality and hash code.
        [InlineData("1.2.3.4", "1.2.3.4.0")]
        [InlineData("1.2.3.4", "1.2.3.4.0.0")]
        [InlineData("18.4_1", "18.4.1")]
        public void TestVersionEqualityIgnoresTrailingZeroSegments(string left, string right)
        {
            CoreTools.Version a = CoreTools.VersionStringToStruct(left);
            CoreTools.Version b = CoreTools.VersionStringToStruct(right);

            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        }

        [Theory]
        [InlineData("2026.1.0", "2026.1.0")]
        [InlineData("2026.1.0.0", "2026.1.0")]
        [InlineData("2026.1.0.5", "2026.1.0")]
        [InlineData("2026.1", "2026.1")]
        public void TestNormalizeVersionForComparison(string rawVersion, string expected)
        {
            var version = System.Version.Parse(rawVersion);
            var normalized = CoreTools.NormalizeVersionForComparison(version);

            Assert.Equal(System.Version.Parse(expected), normalized);
        }

        [Theory]
        [InlineData("Hello World", "Hello World")]
        [InlineData("Hello; World", "Hello World")]
        [InlineData("\"Hello; World\"", "Hello World")]
        [InlineData("'Hello; World'", "Hello World")]
        [InlineData("query\";start cmd.exe", "querystart cmd.exe")]
        [InlineData("query;start /B program.exe", "querystart B program.exe")]
        [InlineData(";&|<>%\"e'~?/\\`", "e")]
        public void TestSafeQueryString(string query, string expected)
        {
            Assert.Equal(CoreTools.EnsureSafeQueryString(query), expected);
        }

        [Fact]
        public void TestEnvVariableCreation()
        {
            const string ENV1 = "NONEXISTENTENVVARIABLE";
            Environment.SetEnvironmentVariable(ENV1, null, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(ENV1, null, EnvironmentVariableTarget.User);

            ProcessStartInfo oldInfo = CoreTools.UpdateEnvironmentVariables();
            oldInfo.Environment.TryGetValue(ENV1, out string? result);
            Assert.Null(result);

            Environment.SetEnvironmentVariable(ENV1, "randomval", EnvironmentVariableTarget.User);

            ProcessStartInfo newInfo1 = CoreTools.UpdateEnvironmentVariables();
            newInfo1.Environment.TryGetValue(ENV1, out string? result2);
            Assert.Equal("randomval", result2);

            Environment.SetEnvironmentVariable(ENV1, null, EnvironmentVariableTarget.User);
            ProcessStartInfo newInfo2 = CoreTools.UpdateEnvironmentVariables();
            newInfo2.Environment.TryGetValue(ENV1, out string? result3);
            Assert.Null(result3);
        }

        [Fact]
        public void TestEnvVariableReplacement()
        {
            const string ENV = "TMP";

            var expected = Environment.GetEnvironmentVariable(ENV, EnvironmentVariableTarget.User);

            ProcessStartInfo info = CoreTools.UpdateEnvironmentVariables();
            info.Environment.TryGetValue(ENV, out string? result);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void TestEnvVariableJuxtaposition()
        {
            const string ENV = "PATH";

            var oldpath =
                Environment.GetEnvironmentVariable(ENV, EnvironmentVariableTarget.Machine)
                + ";"
                + Environment.GetEnvironmentVariable(ENV, EnvironmentVariableTarget.User);

            ProcessStartInfo info = CoreTools.UpdateEnvironmentVariables();
            info.Environment.TryGetValue(ENV, out string? result);
            Assert.Equal(oldpath, result);
        }

        [Theory]
        [InlineData(10, 33, "hello", "[###.......] 33% (hello)")]
        [InlineData(20, 37, null, "[#######.............] 37%")]
        [InlineData(10, 0, "", "[..........] 0% ()")]
        [InlineData(10, 100, "3/3", "[##########] 100% (3/3)")]
        public void TestTextProgressbarGenerator(
            int length,
            int progress,
            string? extra,
            string? expected
        )
        {
            Assert.Equal(CoreTools.TextProgressGenerator(length, progress, extra), expected);
        }

        [Theory]
        [InlineData(0, 1, "0 Bytes")]
        [InlineData(10, 1, "10 Bytes")]
        [InlineData(1024 * 34, 0, "34 KB")]
        [InlineData(65322450, 3, "62.296 MB")]
        [InlineData(65322450000, 3, "60.836 GB")]
        [InlineData(65322450000000, 3, "59.410 TB")]
        public void TestFormatSize(long size, int decPlaces, string expected)
        {
            Assert.Equal(CoreTools.FormatAsSize(size, decPlaces).Replace(',', '.'), expected);
        }

        private static string CreateTemporaryDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), $"UniGetUI-ToolsTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        private static void CreateCommandFile(string path)
        {
            File.WriteAllText(path, string.Empty);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead
                        | UnixFileMode.UserWrite
                        | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead
                        | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead
                        | UnixFileMode.OtherExecute
                );
            }
        }
    }
}
