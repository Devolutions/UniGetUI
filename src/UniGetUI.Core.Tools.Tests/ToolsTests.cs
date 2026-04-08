using System.Diagnostics;
using UniGetUI.Core.Language;

namespace UniGetUI.Core.Tools.Tests
{
    public class ToolsTests
    {
        [Theory]
        [InlineData("NonExistentString", false)]
        [InlineData(" ", false)]
        [InlineData("", false)]
        [InlineData("@@", false)]
        [InlineData("0 packages found", true)]
        [InlineData("Add packages or open an existing bundle", true)]
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
        [InlineData("https://marticliment.com/resources/unigetui.png", 19788)]
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
        [InlineData("4.0.0.1.0", 4, 0, 0, 10)]
        [InlineData("4.0.0.1.05", 4, 0, 0, 105)]
        [InlineData("2024.30.04.1223", 2024, 30, 4, 1223)]
        [InlineData("0.0", 0, 0, 0, 0)]
        public void TestGetVersionStringAsFloat(string version, int i1, int i2, int i3, int i4)
        {
            CoreTools.Version v = CoreTools.VersionStringToStruct(version);
            Assert.Equal(i1, v.Major);
            Assert.Equal(i2, v.Minor);
            Assert.Equal(i3, v.Patch);
            Assert.Equal(i4, v.Remainder);
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
        public void TestEnvVariableYuxtaposition()
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
