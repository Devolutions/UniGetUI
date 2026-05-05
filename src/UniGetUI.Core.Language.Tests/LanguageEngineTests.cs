using UniGetUI.Core.Data;
using UniGetUI.PackageEngine.Enums;

namespace UniGetUI.Core.Language.Tests
{
    public class LanguageEngineTests
    {
        [Theory]
        [InlineData("ca", "Subsistema Android")]
        [InlineData("es", "Subsistema de Android")]
        [InlineData("uk", "Підсистема Android")]
        public void TestLoadingLanguage(string language, string translation)
        {
            LanguageEngine engine = new();

            engine.LoadLanguage(language);
            Assert.Equal(translation, engine.Translate("Android Subsystem"));
        }

        [Fact]
        public void TestLoadingLanguageForNonExistentKey()
        {
            //arrange
            LanguageEngine engine = new();
            engine.LoadLanguage("es");
            //act
            string NONEXISTENT_KEY = "This is a nonexistent key thay should be returned as-is";
            //assert
            Assert.Equal(NONEXISTENT_KEY, engine.Translate(NONEXISTENT_KEY));
        }

        [Theory]
        [InlineData("en", "UniGetUI Log", "UniGetUI")]
        [InlineData("ca", "Registre de l'UniGetUI", "UniGetUI")]
        public void TestUniGetUIRefactoring(
            string language,
            string uniGetUILogTranslation,
            string uniGetUITranslation
        )
        {
            LanguageEngine engine = new();

            engine.LoadLanguage(language);
            Assert.Equal(uniGetUILogTranslation, engine.Translate("UniGetUI Log"));
            Assert.Equal(uniGetUITranslation, engine.Translate("UniGetUI"));
        }

        [Fact]
        public void LocalFallbackTest()
        {
            LanguageEngine engine = new();
            engine.LoadLanguage("random-nonexistent-language");
            Assert.Equal("en", engine.Locale);
        }

        [Fact]
        public void TestLoadingUkrainianSpecificTranslation()
        {
            LanguageEngine engine = new();

            engine.LoadLanguage("uk");
            Assert.Equal("Підсистема Android", engine.Translate("Android Subsystem"));
        }

        [Fact]
        public void TestLoadingLegacyUkrainianAlias()
        {
            LanguageEngine engine = new();

            engine.LoadLanguage("ua");
            Assert.Equal("uk", engine.Locale);
        }

        [Fact]
        public void TestLoadingLanguageIgnoresCachedOverrides()
        {
            string cachedLangFile = Path.Join(CoreData.UniGetUICacheDirectory_Lang, "lang_en.json");
            string? previousContents = File.Exists(cachedLangFile)
                ? File.ReadAllText(cachedLangFile)
                : null;

            Directory.CreateDirectory(CoreData.UniGetUICacheDirectory_Lang);
            File.WriteAllText(
                cachedLangFile,
                """
                {
                                    "Starting operation...": "Cached override should be ignored"
                }
                """
            );

            try
            {
                LanguageEngine engine = new();

                Dictionary<string, string> langFile = engine.LoadLanguageFile("en");
                Assert.Equal("Starting operation...", langFile["Starting operation..."]);
            }
            finally
            {
                if (previousContents is not null)
                {
                    File.WriteAllText(cachedLangFile, previousContents);
                }
                else if (File.Exists(cachedLangFile))
                {
                    File.Delete(cachedLangFile);
                }
            }
        }

        /*
        [Fact]
        public async Task TestDownloadUpdatedTranslationsAsync()
        {
            string expected_file = Path.Join(CoreData.UniGetUICacheDirectory_Lang, "lang_ca.json");
            if (File.Exists(expected_file))
                File.Delete(expected_file);

            LanguageEngine engine = new();
            engine.LoadLanguage("ca");
            await engine.DownloadUpdatedLanguageFile("ca");

            Assert.True(File.Exists(expected_file), "The updated file was not created");
            File.Delete(expected_file);
        }
        */
    }
}
