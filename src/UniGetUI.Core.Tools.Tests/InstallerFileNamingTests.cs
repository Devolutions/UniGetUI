using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI.Core.Tools.Tests;

public class InstallerFileNamingTests : IDisposable
{
    private readonly string _testRoot;

    public InstallerFileNamingTests()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            nameof(InstallerFileNamingTests),
            Guid.NewGuid().ToString("N")
        );
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();
    }

    public void Dispose()
    {
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("", InstallerNameScheme.PublisherName)]
    [InlineData("   ", InstallerNameScheme.PublisherName)]
    [InlineData("nonsense", InstallerNameScheme.PublisherName)]
    [InlineData("publisher", InstallerNameScheme.PublisherName)]
    [InlineData("NAME_VERSION", InstallerNameScheme.NameAndVersion)]
    [InlineData(" id_version ", InstallerNameScheme.IdAndVersion)]
    [InlineData("publisher_version", InstallerNameScheme.PublisherNameAndVersion)]
    public void ResolveScheme_MapsStoredValue(string stored, InstallerNameScheme expected)
    {
        Settings.SetValue(Settings.K.InstallerFileNameScheme, stored);

        Assert.Equal(expected, InstallerFileNaming.ResolveScheme());
    }

    [Theory]
    [InlineData("SpotifyFullSetupX64.exe", "SpotifyFullSetupX64.exe")]
    [InlineData("jre-8u451-windows-x64", "jre-8u451-windows-x64")]
    [InlineData("", "Spotify")]
    public void Build_PublisherNameSchemeKeepsPublisherFileName(string publisherName, string expected)
    {
        Assert.Equal(
            expected,
            InstallerFileNaming.Build(
                publisherName,
                "Spotify",
                "Spotify.Spotify",
                "1.2.68",
                "exe",
                InstallerNameScheme.PublisherName
            )
        );
    }

    [Theory]
    [InlineData(InstallerNameScheme.NameAndVersion, "Spotify_1.2.68.exe")]
    [InlineData(InstallerNameScheme.IdAndVersion, "Spotify.Spotify_1.2.68.exe")]
    [InlineData(InstallerNameScheme.PublisherNameAndVersion, "SpotifyFullSetupX64_1.2.68.exe")]
    public void Build_VersionedSchemesComposeExpectedName(
        InstallerNameScheme scheme,
        string expected
    )
    {
        Assert.Equal(
            expected,
            InstallerFileNaming.Build(
                "SpotifyFullSetupX64.exe",
                "Spotify",
                "Spotify.Spotify",
                "1.2.68",
                "exe",
                scheme
            )
        );
    }

    [Theory]
    [InlineData("exe", "Node_20.11.1.exe")]
    [InlineData("nullsoft", "Node_20.11.1.exe")]
    [InlineData("msix", "Node_20.11.1.msix")]
    [InlineData("appx", "Node_20.11.1.appx")]
    [InlineData(".exe", "Node_20.11.1.exe")]
    [InlineData("Homebrew Formula", "Node_20.11.1")]
    public void Build_MapsInstallerTypeToItsOwnExtension(string installerType, string expected)
    {
        Assert.Equal(
            expected,
            InstallerFileNaming.Build(
                "download",
                "Node",
                "OpenJS.NodeJS",
                "20.11.1",
                installerType,
                InstallerNameScheme.NameAndVersion
            )
        );
    }

    [Theory]
    [InlineData("setup.tar.gz", "Node_20.11.1.tar.gz")]
    [InlineData("setup.msixbundle", "Node_20.11.1.msixbundle")]
    [InlineData("setup.appinstaller", "Node_20.11.1.appinstaller")]
    [InlineData("download", "Node_20.11.1.exe")]
    [InlineData("installer_20.11.1", "Node_20.11.1.exe")]
    public void Build_ResolvesExtensionFromPublisherNameOrInstallerType(
        string publisherName,
        string expected
    )
    {
        Assert.Equal(
            expected,
            InstallerFileNaming.Build(
                publisherName,
                "Node",
                "OpenJS.NodeJS",
                "20.11.1",
                "nullsoft",
                InstallerNameScheme.NameAndVersion
            )
        );
    }

    [Theory]
    [InlineData(null, "Node.exe")]
    [InlineData("", "Node.exe")]
    [InlineData("Unknown", "Node.exe")]
    [InlineData("unknown", "Node.exe")]
    [InlineData("Latest", "Node.exe")]
    [InlineData("Dernière", "Node.exe")]
    [InlineData("   ", "Node.exe")]
    [InlineData("2.4 build 7", "Node_2.4_build_7.exe")]
    public void Build_SkipsUnusableVersions(string? version, string expected)
    {
        Assert.Equal(
            expected,
            InstallerFileNaming.Build(
                "setup.exe",
                "Node",
                "OpenJS.NodeJS",
                version,
                "exe",
                InstallerNameScheme.NameAndVersion
            )
        );
    }

    [Fact]
    public void Build_DoesNotRepeatAVersionAlreadyPresentInThePublisherName()
    {
        Assert.Equal(
            "jre-8u451-windows-x64.exe",
            InstallerFileNaming.Build(
                "jre-8u451-windows-x64.exe",
                "Java Runtime Environment",
                "Oracle.JavaRuntimeEnvironment",
                "8u451",
                "exe",
                InstallerNameScheme.PublisherNameAndVersion
            )
        );
    }

    [Fact]
    public void Build_StripsCharactersThatCannotBeUsedInFileNames()
    {
        Assert.Equal(
            "Foo Bar Baz_1.0.exe",
            InstallerFileNaming.Build(
                "setup.exe",
                "Foo: Bar / Baz",
                "Foo.Bar",
                "1.0",
                "exe",
                InstallerNameScheme.NameAndVersion
            )
        );
    }

    [Theory]
    [InlineData(InstallerNameScheme.NameAndVersion, "Foo.Bar_1.0.exe")]
    [InlineData(InstallerNameScheme.PublisherNameAndVersion, "Foo.Bar_1.0.exe")]
    public void Build_FallsBackToAnotherFieldWhenTheChosenOneIsEmpty(
        InstallerNameScheme scheme,
        string expected
    )
    {
        Assert.Equal(
            expected,
            InstallerFileNaming.Build("", "", "Foo.Bar", "1.0", "exe", scheme)
        );
    }

    [Fact]
    public void Build_NeverReturnsAnEmptyName()
    {
        Assert.Equal(
            "installer_1.0",
            InstallerFileNaming.Build(null, null, null, "1.0", null, InstallerNameScheme.NameAndVersion)
        );
        Assert.Equal(
            "installer",
            InstallerFileNaming.Build(null, null, null, null, null, InstallerNameScheme.PublisherName)
        );
    }

    [Theory]
    [InlineData("setup.exe", ".exe")]
    [InlineData("setup.tar.gz", ".tar.gz")]
    [InlineData("archive.7z", ".7z")]
    [InlineData("bundle.appinstaller", ".appinstaller")]
    [InlineData("release.1234567890123456789", "")]
    [InlineData("installer_1.2.3", "")]
    [InlineData("download", "")]
    public void ExtractExtension_OnlyAcceptsExtensionShapedTails(string fileName, string expected)
    {
        Assert.Equal(expected, InstallerFileNaming.ExtractExtension(fileName));
    }
}
