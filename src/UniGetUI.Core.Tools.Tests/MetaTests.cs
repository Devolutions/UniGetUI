using System.Text.RegularExpressions;

namespace UniGetUI.Core.Tools.Tests;

public class MetaTests
{
    [Fact]
    public void TestJsonSerializationOptions()
    {
        // This test ensures that any json operation has the proper serialization options set (required for TRIM support)
        var solutionDirectory = FindRepositoryRoot();
        var csFiles = Directory
            .GetFiles(solutionDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
                !file.Contains(@"bin\")
                && !file.Contains(@"obj\")
                && !file.EndsWith(".g.cs")
                && !file.EndsWith("Tests.cs")
            );

        foreach (var file in csFiles)
        {
            var lines = File.ReadAllLines(file);
            var jsonSerCount = lines.Count(x => x.Contains("JsonSerializer.Serialize"));
            var jsonDeserCount = lines.Count(x => x.Contains("JsonSerializer.Deserialize"));
            var trimSafeJsonMetadataCount = lines.Count(x =>
                x.Contains("SerializationHelpers.DefaultOptions")
                || x.Contains("SerializationHelpers.ImportBundleOptions")
                || x.Contains("SerializationOptions")
                || x.Contains("JsonTypeInfo")
                || x.Contains("JsonSerializerContext")
                || x.Contains("JsonSourceGenerationOptions")
                || x.Contains("GetTypeInfo")
                || x.Contains("typeInfo")
            );
            Assert.True(
                (jsonSerCount + jsonDeserCount) <= trimSafeJsonMetadataCount,
                $"Failing on {file}. The specified file does not serialize and/or deserialize JSON with"
                    + $" explicit trim-safe JSON metadata"
            );
        }
    }

    [Fact]
    public void TestHttpClientInstantiation()
    {
        // This test ensures that any instantiation of HttpClient contains at least one empty line after it
        var solutionDirectory = FindRepositoryRoot();
        var csFiles = Directory
            .GetFiles(solutionDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
                !file.Contains(@"bin\")
                && !file.Contains(@"obj\")
                && !file.EndsWith(".g.cs")
                && !file.EndsWith("Tests.cs")
                && !file.EndsWith("LanguageEngine.cs")
            );

        foreach (var file in csFiles)
        {
            var fileContent = File.ReadAllText(file);
            var match = Regex.Match(fileContent, "new HttpClient ?\\(\\)");
            var match2 = Regex.Match(fileContent, "HttpClient [a-zA-Z0-9_]+ ?= ?new ?\\(\\)");
            Assert.False(
                match.Success || match2.Success,
                $"File {file} has an incorrect instantiation of HttpClient, that does not pass the "
                    + $"compulsory CoreTools.GenericHttpClientParameters param"
            );
        }
    }

    [Fact]
    public void TestJsonSerializerContextsDoNotReuseSharedOptions()
    {
        var solutionDirectory = FindRepositoryRoot();
        var csFiles = Directory
            .GetFiles(solutionDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
                !file.Contains(@"bin\")
                && !file.Contains(@"obj\")
                && !file.EndsWith(".g.cs")
                && !file.EndsWith("Tests.cs")
            );

        Regex forbiddenPattern = new(
            @"JsonContext\s+\w+\s*=\s*new\(\s*SerializationHelpers\.DefaultOptions\s*\)",
            RegexOptions.Multiline
        );

        foreach (var file in csFiles)
        {
            var fileContent = File.ReadAllText(file);
            Assert.False(
                forbiddenPattern.IsMatch(fileContent),
                $"File {file} reuses SerializationHelpers.DefaultOptions when constructing a generated JsonSerializerContext. Clone the options first to avoid rebinding the shared resolver."
            );
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? currentDirectory = new(AppDomain.CurrentDomain.BaseDirectory);

        while (currentDirectory is not null)
        {
            if (
                File.Exists(Path.Join(currentDirectory.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Join(currentDirectory.FullName, "src"))
            )
            {
                return currentDirectory.FullName;
            }

            currentDirectory = currentDirectory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Unable to locate the UniGetUI repository root from the test output directory."
        );
    }
}
