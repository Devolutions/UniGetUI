using System.IO.Compression;
using System.Text;
using UniGetUI.PackageEngine.Managers.Generic.NuGet.Internal;

namespace UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

public sealed class LocalNuGetFeedBuilder : IDisposable
{
    public LocalNuGetFeedBuilder(string? folderName = null)
    {
        Directory = Path.Join(Path.GetTempPath(), folderName ?? Path.GetRandomFileName());
        System.IO.Directory.CreateDirectory(Directory);
        NuGetLocalFeed.ClearCache();
    }

    public string Directory { get; }

    public string WritePackage(
        string id,
        string version,
        string? folder = null,
        string description = "A package",
        string authors = "Example Ltd",
        string tags = "tooling",
        string? iconUrl = null,
        string? dependencyId = null,
        string? iconFile = null,
        byte[]? iconBytes = null
    )
    {
        string target = folder is null ? Directory : Path.Join(Directory, folder);
        System.IO.Directory.CreateDirectory(target);

        string file = Path.Join(target, $"{id}.{version}.nupkg");
        string nuspec = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2015/06/nuspec.xsd">
              <metadata>
                <id>{id}</id>
                <version>{version}</version>
                <title>{id}</title>
                <authors>{authors}</authors>
                <description>{description}</description>
                <tags>{tags}</tags>
                <projectUrl>https://example.test/package</projectUrl>
                <license type="expression">MIT</license>
                {(iconUrl is null ? string.Empty : $"<iconUrl>{iconUrl}</iconUrl>")}
                {(iconFile is null ? string.Empty : $"<icon>{iconFile}</icon>")}
                {(
                dependencyId is null
                    ? string.Empty
                    : $"""
                        <dependencies>
                          <group targetFramework="net10.0">
                            <dependency id="{dependencyId}" version="[1.2.0, )" />
                          </group>
                        </dependencies>
                        """
            )}
              </metadata>
            </package>
            """;

        using (FileStream stream = File.Create(file))
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
        {
            using (Stream entry = archive.CreateEntry($"{id}.nuspec").Open())
                entry.Write(Encoding.UTF8.GetBytes(nuspec));

            if (iconFile is not null && iconBytes is not null)
            {
                using Stream icon = archive.CreateEntry(iconFile).Open();
                icon.Write(iconBytes);
            }
        }

        NuGetLocalFeed.ClearCache();
        return file;
    }

    public string WriteRawPackage(string fileName, string nuspec)
    {
        string file = Path.Join(Directory, fileName);

        using (FileStream stream = File.Create(file))
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
        {
            using Stream entry = archive.CreateEntry("package.nuspec").Open();
            entry.Write(Encoding.UTF8.GetBytes(nuspec));
        }

        NuGetLocalFeed.ClearCache();
        return file;
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, true);
        }
        catch (IOException) { }
    }
}
