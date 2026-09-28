using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using UniGetUI.Core.Classes;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Enums;

namespace UniGetUI.Core.Language
{
    public static class LanguageData
    {
        private static Person[]? __translators_list;

        public static Person[] TranslatorsList
        {
            get
            {
                if (__translators_list is null)
                {
                    __translators_list = LoadLanguageTranslatorList();
                }

                return __translators_list;
            }
        }

        private static ReadOnlyDictionary<string, string>? __language_reference;
        public static ReadOnlyDictionary<string, string> LanguageReference
        {
            get
            {
                if (__language_reference is null)
                {
                    __language_reference = LoadLanguageReference();
                }

                return __language_reference;
            }
        }

        private static ReadOnlyDictionary<string, string>? __translation_percentages;
        public static ReadOnlyDictionary<string, string> TranslationPercentages
        {
            get
            {
                if (__translation_percentages is null)
                {
                    __translation_percentages = LoadTranslationPercentages();
                }

                return __translation_percentages;
            }
        }

        private static ReadOnlyDictionary<string, string> LoadTranslationPercentages()
        {
            try
            {
                if (
                    JsonNode.Parse(
                        BundledAssets.ReadAllText("Data/TranslatedPercentages.json")
                    )
                    is JsonObject val
                )
                {
                    return new(
                        val.ToDictionary(x => x.Key, x => (x.Value ?? ("404%" + x.Key)).ToString())
                    );
                }

                return new(new Dictionary<string, string>());
            }
            catch (Exception ex)
            {
                Logger.Error("Could not load TranslatedPercentages.json from disk");
                Logger.Error(ex);
                return new(new Dictionary<string, string>());
            }
        }

        private static ReadOnlyDictionary<string, string> LoadLanguageReference()
        {
            try
            {
                if (
                    JsonNode.Parse(
                        BundledAssets.ReadAllText("Data/LanguagesReference.json")
                    )
                    is JsonObject val
                )
                {
                    return new(
                        val.ToDictionary(
                            x => x.Key,
                            x => (x.Value ?? ("NoNameLang_" + x.Key)).ToString()
                        )
                    );
                }

                return new(new Dictionary<string, string>());
            }
            catch (Exception ex)
            {
                Logger.Error("Could not load LanguagesReference.json from disk");
                Logger.Error(ex);
                return new(new Dictionary<string, string>());
            }
        }

        private static Person[] LoadLanguageTranslatorList()
        {
            try
            {
                string JsonContents = BundledAssets.ReadAllText("Data/Translators.json");

                if (JsonNode.Parse(JsonContents) is not JsonObject TranslatorsInfo)
                {
                    return [];
                }

                List<Person> result = [];
                foreach (KeyValuePair<string, JsonNode?> langKey in TranslatorsInfo)
                {
                    if (!LanguageReference.ContainsKey(langKey.Key))
                    {
                        Logger.Warn(
                            $"Language {langKey.Key} not in list, maybe has not been added yet?"
                        );
                        continue;
                    }

                    JsonArray TranslatorsForLang = (langKey.Value ?? new JsonArray()).AsArray();
                    bool LangShown = false;
                    foreach (JsonNode? translator in TranslatorsForLang)
                    {
                        if (translator is null)
                        {
                            continue;
                        }

                        Uri? url = null;
                        if (translator["link"] is not null && translator["link"]?.ToString() != "")
                        {
                            url = new Uri((translator["link"] ?? "").ToString());
                        }

                        Person person = new(
                            Name: (url is not null ? "@" : "")
                                + (translator["name"] ?? "").ToString(),
                            ProfilePicture: url is not null
                                ? new Uri(url.ToString() + ".png")
                                : null,
                            GitHubUrl: url,
                            Language: !LangShown ? LanguageReference[langKey.Key] : ""
                        );
                        LangShown = true;
                        result.Add(person);
                    }
                }

                return result.ToArray();
            }
            catch (Exception ex)
            {
                Logger.Error("Could not load Translators.json from disk");
                Logger.Error(ex);
                return [];
            }
        }
    }

    public static class CommonTranslations
    {
        public static readonly Dictionary<string, string> ScopeNames = new()
        {
            { PackageScope.Global, "Machine | Global" },
            { PackageScope.Local, "User | Local" },
        };

        public static readonly Dictionary<string, string> InvertedScopeNames = new()
        {
            { "Machine | Global", PackageScope.Global },
            { "User | Local", PackageScope.Local },
        };
    }
}
