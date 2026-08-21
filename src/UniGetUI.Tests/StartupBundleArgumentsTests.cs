using UniGetUI.Interface;
using UniGetUI.Shared;

namespace UniGetUI.Tests;

public sealed class StartupBundleArgumentsTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        nameof(StartupBundleArgumentsTests),
        Guid.NewGuid().ToString("N")
    );

    public StartupBundleArgumentsTests()
    {
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private string CreateFile(string name)
    {
        string path = Path.Combine(_testRoot, name);
        File.WriteAllText(path, "{}");
        return path;
    }

    [Theory]
    [InlineData("bundle.ubundle")]
    [InlineData("bundle.json")]
    [InlineData("bundle.yaml")]
    [InlineData("bundle.xml")]
    [InlineData("bundle.UBUNDLE")]
    public void Resolve_AcceptsSupportedExtensionsAsRelativePaths(string name)
    {
        string expected = CreateFile(name);

        List<string> bundles = StartupBundleArguments.Resolve([name], _testRoot);

        Assert.Equal([expected], bundles);
    }

    [Fact]
    public void Resolve_AcceptsFullyQualifiedPaths()
    {
        string expected = CreateFile("bundle.ubundle");

        List<string> bundles = StartupBundleArguments.Resolve([expected], Path.GetTempPath());

        Assert.Equal([expected], bundles);
    }

    [Fact]
    public void Resolve_StripsSurroundingQuotes()
    {
        string expected = CreateFile("bundle.ubundle");

        List<string> bundles = StartupBundleArguments.Resolve([$"\"{expected}\""], _testRoot);

        Assert.Equal([expected], bundles);
    }

    [Fact]
    public void Resolve_IgnoresMissingFiles()
    {
        Assert.Empty(StartupBundleArguments.Resolve(["missing.ubundle"], _testRoot));
    }

    [Fact]
    public void Resolve_IgnoresUnsupportedExtensions()
    {
        CreateFile("bundle.txt");

        Assert.Empty(StartupBundleArguments.Resolve(["bundle.txt"], _testRoot));
    }

    [Fact]
    public void Resolve_IgnoresFlagsAndTheirValues()
    {
        string bundle = CreateFile("bundle.ubundle");

        List<string> bundles = StartupBundleArguments.Resolve(
            ["--daemon", IpcTransportOptions.CliNamedPipeArgument, bundle],
            _testRoot
        );

        Assert.Empty(bundles);
    }

    [Fact]
    public void Resolve_FindsBundlesAfterAFlagValuePair()
    {
        string bundle = CreateFile("bundle.ubundle");

        List<string> bundles = StartupBundleArguments.Resolve(
            [IpcTransportOptions.CliTcpPortArgument, "7058", "bundle.ubundle"],
            _testRoot
        );

        Assert.Equal([bundle], bundles);
    }

    [Fact]
    public void Resolve_ReturnsEveryBundleInOrder()
    {
        string first = CreateFile("first.ubundle");
        string second = CreateFile("second.json");

        List<string> bundles = StartupBundleArguments.Resolve(
            ["first.ubundle", "--daemon", "second.json"],
            _testRoot
        );

        Assert.Equal([first, second], bundles);
    }

    [Fact]
    public void Normalize_RewritesRelativeBundlePathsAndLeavesOtherArgumentsUntouched()
    {
        string bundle = CreateFile("bundle.ubundle");

        string[] normalized = StartupBundleArguments.Normalize(
            ["--daemon", "bundle.ubundle", "missing.ubundle"],
            _testRoot
        );

        Assert.Equal(["--daemon", bundle, "missing.ubundle"], normalized);
    }

    [Fact]
    public void Normalize_KeepsEmptyArgumentListsEmpty()
    {
        Assert.Empty(StartupBundleArguments.Normalize([], _testRoot));
    }
}
