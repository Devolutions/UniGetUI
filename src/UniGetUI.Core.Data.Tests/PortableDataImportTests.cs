using UniGetUI.Core.Logging;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace UniGetUI.Core.Data.Tests
{
    public sealed class PortableDataImportTests : IDisposable
    {
        private readonly string _testRoot;

        public PortableDataImportTests()
        {
            _testRoot = Path.Combine(
                Path.GetTempPath(),
                $"UniGetUI-PortableImportTests-{Guid.NewGuid():N}"
            );
            Directory.CreateDirectory(_testRoot);
        }

        public void Dispose()
        {
            CoreData.TEST_DataDirectoryOverride = null;
            CoreData.TEST_PerUserDataDirectoryOverride = null;
            AppPaths.TEST_PortableDataDirectoryOverride = null;

            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, true);
            }
        }

        private string CreateSource()
        {
            string source = Path.Combine(_testRoot, "PerUser");
            Directory.CreateDirectory(Path.Combine(source, "Configuration"));
            Directory.CreateDirectory(Path.Combine(source, "InstallationOptions"));
            Directory.CreateDirectory(Path.Combine(source, "CachedMedia"));
            File.WriteAllText(Path.Combine(source, "Configuration", "EnableScoop"), "");
            File.WriteAllText(Path.Combine(source, "Configuration", "Settings.json"), "{}");
            File.WriteAllText(Path.Combine(source, "InstallationOptions", "winget.pkg.json"), "{}");
            File.WriteAllText(Path.Combine(source, "CachedMedia", "icon.png"), "not-a-real-icon");
            return source;
        }

        private string UsePortableDestination()
        {
            string destination = Path.Combine(_testRoot, "Portable");
            Directory.CreateDirectory(destination);
            AppPaths.TEST_PortableDataDirectoryOverride = destination;
            CoreData.TEST_DataDirectoryOverride = destination;
            return destination;
        }

        [Fact]
        public void ImportCopiesUserDataButNotCaches()
        {
            string source = CreateSource();
            string destination = UsePortableDestination();

            int copied = PortableDataImport.Import(source);

            Assert.Equal(3, copied);
            Assert.True(File.Exists(Path.Combine(destination, "Configuration", "EnableScoop")));
            Assert.True(File.Exists(Path.Combine(destination, "Configuration", "Settings.json")));
            Assert.True(File.Exists(Path.Combine(destination, "InstallationOptions", "winget.pkg.json")));
            Assert.False(Directory.Exists(Path.Combine(destination, "CachedMedia")));
        }

        [Fact]
        public void ImportLeavesTheSourceUntouched()
        {
            string source = CreateSource();
            UsePortableDestination();

            PortableDataImport.Import(source);

            Assert.True(File.Exists(Path.Combine(source, "Configuration", "EnableScoop")));
            Assert.True(File.Exists(Path.Combine(source, "InstallationOptions", "winget.pkg.json")));
        }

        [Fact]
        public void ImportNeverOverwritesAnExistingFile()
        {
            string source = CreateSource();
            string destination = UsePortableDestination();
            Directory.CreateDirectory(Path.Combine(destination, "Configuration"));
            File.WriteAllText(Path.Combine(destination, "Configuration", "Settings.json"), "portable");

            int copied = PortableDataImport.Import(source);

            Assert.Equal(2, copied);
            Assert.Equal("portable", File.ReadAllText(Path.Combine(destination, "Configuration", "Settings.json")));
        }

        [Fact]
        public void TheDefaultBackupFolderMovesIntoThePortableFolder()
        {
            string destination = UsePortableDestination();

            Assert.Equal(Path.Join(destination, "Backups"), CoreData.UniGetUI_DefaultBackupDirectory);
        }

        [Fact]
        public void TheDefaultBackupFolderStaysInDocumentsWhenNotPortable()
        {
            AppPaths.TEST_PortableDataDirectoryOverride = null;

            Assert.DoesNotContain("Backups", CoreData.UniGetUI_DefaultBackupDirectory);
        }

        [Fact]
        public void ASourceIsOnlyOfferedOnTheFirstRunOfAPortableFolder()
        {
            string source = CreateSource();
            CoreData.TEST_PerUserDataDirectoryOverride = source;
            UsePortableDestination();

            Assert.Equal(
                source,
                PortableDataImport.FindImportableSource(isFirstPortableRun: true));
            Assert.Null(PortableDataImport.FindImportableSource(isFirstPortableRun: false));
        }

        [Fact]
        public void NoSourceIsOfferedWhenThePerUserDirectoryHasNoSettings()
        {
            string empty = Path.Combine(_testRoot, "Empty");
            Directory.CreateDirectory(empty);
            CoreData.TEST_PerUserDataDirectoryOverride = empty;
            UsePortableDestination();

            Assert.Null(PortableDataImport.FindImportableSource(isFirstPortableRun: true));
        }

        [Fact]
        public void NoSourceIsOfferedWhenNotPortable()
        {
            AppPaths.TEST_PortableDataDirectoryOverride = null;

            Assert.Null(PortableDataImport.FindImportableSource());
        }
    }
}
