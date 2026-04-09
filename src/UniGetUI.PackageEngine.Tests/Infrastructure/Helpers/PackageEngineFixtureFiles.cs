namespace UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

public static class PackageEngineFixtureFiles
{
    public static string RootPath => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string GetPath(string relativePath)
    {
        var fullPath = Path.Combine(RootPath, relativePath);
        Assert.True(File.Exists(fullPath), $"Expected fixture file to exist: {fullPath}");
        return fullPath;
    }

    public static string ReadAllText(string relativePath)
    {
        return File.ReadAllText(GetPath(relativePath));
    }

    public static byte[] ReadAllBytes(string relativePath)
    {
        return File.ReadAllBytes(GetPath(relativePath));
    }
}
