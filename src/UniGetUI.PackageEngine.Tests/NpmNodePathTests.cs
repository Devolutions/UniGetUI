using UniGetUI.PackageEngine.Managers.NpmManager;

namespace UniGetUI.PackageEngine.Tests;

public sealed class NpmNodePathTests
{
    private static readonly string Separator = Path.PathSeparator.ToString();

    [Fact]
    public void AppendDirectoryToPathAddsAMissingDirectoryAtTheEnd()
    {
        string nvmBin = Path.Join("Users", "tester", ".nvm", "versions", "node", "v24.21.0", "bin");
        string path = string.Join(Separator, "usr-bin", "bin");

        Assert.Equal(
            string.Join(Separator, "usr-bin", "bin", nvmBin),
            Npm.AppendDirectoryToPath(path, nvmBin)
        );
    }

    [Fact]
    public void AppendDirectoryToPathKeepsAPathThatAlreadyHasTheDirectory()
    {
        string nvmBin = Path.Join("Users", "tester", ".nvm", "versions", "node", "v24.21.0", "bin");
        string path = string.Join(Separator, "usr-bin", nvmBin + Path.DirectorySeparatorChar, "bin");

        Assert.Equal(path, Npm.AppendDirectoryToPath(path, nvmBin));
    }

    [Fact]
    public void AppendDirectoryToPathStartsAnEmptyPath()
    {
        Assert.Equal("bin", Npm.AppendDirectoryToPath(null, "bin"));
        Assert.Equal("bin", Npm.AppendDirectoryToPath("", "bin"));
    }
}
