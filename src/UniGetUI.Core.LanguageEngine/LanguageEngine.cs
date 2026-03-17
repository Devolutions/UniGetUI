using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Nodes;
using Jeffijoe.MessageFormat;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Enums;

namespace UniGetUI.Core.Language
{
    public class LanguageEngine
    {
        private Dictionary<string, string> MainLangDict = [];
        public static string SelectedLocale = "??";

        [NotNull]
        public string? Locale { get; private set; }

        private MessageFormatter? Formatter;

        public LanguageEngine(string ForceLanguage = "")
        {
            string LangName = Settings.GetValue(Settings.K.PreferredLanguage);
            if (LangName is "default" or "")
            {
                LangName = CultureInfo.CurrentUICulture.ToString().Replace("-", "_");
                if (string.IsNullOrWhiteSpace(LangName))
                {
                    LangName = "en";
                }
            }
            LoadLanguage((ForceLanguage != "") ? ForceLanguage : LangName);
        }

        /// <summary>
        /// Loads the specified language into the current instance
        /// </summary>
        /// <param name="lang">the language code</param>
        public void LoadLanguage(string lang)
        {
            try
            {
                lang = (lang ?? string.Empty).Trim();

                Locale = "en";
                if (LanguageData.LanguageReference.ContainsKey(lang))
                {
                    Locale = lang;
                }
                else if (lang.Length >= 2)
                {
                    string prefix = lang[0..2].Replace("uk", "ua");
                    if (LanguageData.LanguageReference.ContainsKey(prefix))
                    {
                        Locale = prefix;
                    }
                }

                MainLangDict = LoadLanguageFile(Locale);
                Formatter = new() { Locale = Locale.Replace('_', '-') };

                LoadStaticTranslation();
                SelectedLocale = Locale;
                Logger.Info("Loaded language locale: " + Locale);
            }
            catch (Exception ex)
            {
                Logger.Error($"Could not load language file \"{lang}\"");
                Logger.Error(ex);

                // Keep the app functional even if locale resolution fails.
                Locale = "en";
                MainLangDict = LoadLanguageFile(Locale);
                Formatter = new() { Locale = "en" };
                LoadStaticTranslation();
                SelectedLocale = Locale;
            }
        }

        public Dictionary<string, string> LoadLanguageFile(string LangKey)
        {
            try
            {
                string BundledLangFileToLoad = Path.Join(
                    CoreData.UniGetUIExecutableDirectory,
                    "Assets",
                    "Languages",
                    "lang_" + LangKey + ".json"
                );
                JsonObject BundledContents = [];

                if (!File.Exists(BundledLangFileToLoad))
                {
                    Logger.Error(
                        $"Tried to access a non-existing bundled language file! file={BundledLangFileToLoad}"
                    );
                }
                else
                {
                    try
                    {
                        if (
                            JsonNode.Parse(File.ReadAllText(BundledLangFileToLoad))
                            is JsonObject parsedObject
                        )
                            BundledContents = parsedObject;
                        else
                            throw new ArgumentException(
                                $"parsedObject was null for lang file {BundledLangFileToLoad}"
                            );
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(
                            $"Something went wrong when parsing language file {BundledLangFileToLoad}"
                        );
                        Logger.Warn(ex);
                    }
                }

                Dictionary<string, string> LangDict = BundledContents.ToDictionary(
                    x => x.Key,
                    x => x.Value?.ToString() ?? ""
                );

                string CachedLangFileToLoad = Path.Join(
                    CoreData.UniGetUICacheDirectory_Lang,
                    "lang_" + LangKey + ".json"
                );

                if (Settings.Get(Settings.K.DisableLangAutoUpdater))
                {
                    Logger.Warn("User has updated translations disabled");
                }
                else if (!File.Exists(CachedLangFileToLoad))
                {
                    Logger.Warn(
                        $"Tried to access a non-existing cached language file! file={CachedLangFileToLoad}"
                    );
                }
                else
                {
                    try
                    {
                        if (
                            JsonNode.Parse(File.ReadAllText(CachedLangFileToLoad))
                            is JsonObject parsedObject
                        )
                            foreach (
                                var keyval in parsedObject.ToDictionary(x => x.Key, x => x.Value)
                            )
                            {
                                LangDict[keyval.Key] = keyval.Value?.ToString() ?? "";
                            }
                        else
                            throw new ArgumentException(
                                $"parsedObject was null for lang file {CachedLangFileToLoad}"
                            );
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(
                            $"Something went wrong when parsing language file {BundledLangFileToLoad}"
                        );
                        Logger.Warn(ex);
                    }
                }

                if (!Settings.Get(Settings.K.DisableLangAutoUpdater))
                    _ = DownloadUpdatedLanguageFile(LangKey);

                return LangDict;
            }
            catch (Exception e)
            {
                Logger.Error($"LoadLanguageFile Failed for LangKey={LangKey}");
                Logger.Error(e);
                return [];
            }
        }

        /// <summary>
        /// Downloads and saves an updated version of the translations for the specified language.
        /// </summary>
        /// <param name="LangKey">The Id of the language to download</param>
        public async Task DownloadUpdatedLanguageFile(string LangKey)
        {
            try
            {
                Uri NewFile = new(
                    "https://raw.githubusercontent.com/Devolutions/UniGetUI/main/src/UniGetUI.Core.LanguageEngine/Assets/Languages/lang_"
                        + LangKey
                        + ".json"
                );

                HttpClient client = new();
                client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
                string fileContents = await client.GetStringAsync(NewFile);

                if (!Directory.Exists(CoreData.UniGetUICacheDirectory_Lang))
                {
                    Directory.CreateDirectory(CoreData.UniGetUICacheDirectory_Lang);
                }

                File.WriteAllText(
                    Path.Join(CoreData.UniGetUICacheDirectory_Lang, "lang_" + LangKey + ".json"),
                    fileContents
                );

                Logger.ImportantInfo("Lang files were updated successfully from GitHub");
            }
            catch (Exception e)
            {
                Logger.Warn("Could not download updated translations from GitHub");
                Logger.Warn(e);
            }
        }

        public void LoadStaticTranslation()
        {
            CommonTranslations.ScopeNames[PackageScope.Local] = Translate("User | Local");
            CommonTranslations.ScopeNames[PackageScope.Global] = Translate("Machine | Global");

            CommonTranslations.InvertedScopeNames.Clear();
            CommonTranslations.InvertedScopeNames.Add(
                Translate("Machine | Global"),
                PackageScope.Global
            );
            CommonTranslations.InvertedScopeNames.Add(
                Translate("User | Local"),
                PackageScope.Local
            );
        }

        public string Translate(string key)
        {
            if (key == "WingetUI")
            {
                if (
                    MainLangDict.TryGetValue("formerly WingetUI", out var formerly)
                    && formerly != ""
                )
                {
                    return "UniGetUI (" + formerly + ")";
                }

                return "UniGetUI (formerly WingetUI)";
            }

            if (key == "Formerly known as WingetUI")
            {
                return MainLangDict.GetValueOrDefault(key, key);
            }

            if (key is null or "")
            {
                return "";
            }

            if (MainLangDict.TryGetValue(key, out var value) && value != "")
            {
                return value.Replace("WingetUI", "UniGetUI");
            }

            return key.Replace("WingetUI", "UniGetUI");
        }

        public string Translate(string key, Dictionary<string, object?> dict)
        {
            Formatter ??= new() { Locale = (Locale ?? "en").Replace('_', '-') };
            return Formatter.FormatMessage(Translate(key), dict);
        }
    }
}
