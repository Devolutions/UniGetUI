using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UniGetUI.Core.Data;
using UniGetUI.Core.Language;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Views.Dialogs;
using K = Avalonia.Input.Key;
using NAssert = NUnit.Framework.Assert;

namespace UniGetUI.Tui.Tests;

[TestFixture, NonParallelizable]
internal sealed class TuiHelpLocalizationTests : TuiE2ETestBase
{
    private static readonly Dictionary<string, string> Translations = new()
    {
        ["Help"] = "Aide test",
        ["Close"] = "Fermer test",
        ["Keyboard shortcuts"] = "Raccourcis test",
        ["Go to the numbered page tab ({0} where the terminal sends it)"] = "Choisir un onglet ({0} si disponible)",
        ["Next page ({0}: previous)"] = "Page suivante ({0} : précédente)",
        ["Move between the controls of the page"] = "Déplacer le focus entre les contrôles",
        ["letter"] = "lettre",
        ["Menu bar; use the highlighted access letter, then {0} to pick an item"] = "Parcourir les menus avec la lettre puis {0}",
        ["Reload"] = "Recharger test",
        ["Search"] = "Rechercher",
        ["Select all"] = "Tout choisir",
        ["Quit"] = "Quitter test",
        ["Close a dialog or menu, or return to the page's list"] = "Fermer la fenêtre ou revenir",
        ["Package lists"] = "Paquets test",
        ["Select or unselect the package"] = "Sélectionner ou désélectionner un paquet",
        ["Package details"] = "Détails test",
        ["Main action"] = "Action principale test",
        ["Installation options"] = "Options test",
        ["All actions for the package"] = "Actions test",
        ["Install"] = "Installer test",
        ["Update"] = "Mettre à jour test",
        ["Uninstall"] = "Désinstaller test",
        ["Add to bundle"] = "Ajouter au lot test",
        ["Ignore updates"] = "Ignorer les mises à jour test",
        ["Filter by source"] = "Source test",
        ["Sort"] = "Tri test",
        ["Search mode"] = "Mode de recherche test",
        ["Dialogs"] = "Dialogues test",
        ["{0} moves between fields, {1} activates, {2} cancels"] = "{0} navigue, {1} confirme, {2} ferme",
        ["Choice fields"] = "Choix test",
        ["{0} changes the value, {1} lists every value"] = "{0} change le choix, {1} liste les valeurs",
        ["Documentation"] = "Documentation test FR",
        ["Open UniGetUI help"] = "Ouvrir aide test",
        ["Command-line reference"] = "Référence CLI test",
        ["Release notes"] = "Notes de version test",
        ["Report an issue or submit a feature request"] = "Signaler un problème test",
        ["Check for updates"] = "Vérifier les mises à jour test",
        ["The terminal UI does not update itself; updates are installed with the desktop app or your package manager."] = "Installer les mises à jour avec l'application test",
        ["Command line"] = "Ligne de commande test",
        ["Themes"] = "Thèmes test",
    };

    private static readonly string[] ExpectedReference =
    [
        "Alt+1 … Alt+9       Choisir un onglet (Ctrl+1 … Ctrl+9 si disponible)",
        "Ctrl+Tab            Page suivante (Ctrl+Shift+Tab : précédente)",
        "Tab / Shift+Tab     Déplacer le focus entre les contrôles",
        "F10 / Alt+lettre    Parcourir les menus avec la lettre puis ↑ ↓ Enter",
        "F1                  Aide test          F5 / Ctrl+R  Recharger test",
        "Ctrl+F or /         Rechercher        Ctrl+A  Tout choisir      Ctrl+Q  Quitter test",
        "Esc                 Fermer la fenêtre ou revenir",
        "",
        "Paquets test:",
        "  Space             Sélectionner ou désélectionner un paquet",
        "  Enter             Détails test      Ctrl+Enter  Action principale test",
        "  o / Alt+Enter     Options test m  Actions test",
        "  i u x             Installer test / Mettre à jour test / Désinstaller test",
        "  b                 Ajouter au lot test        g  Ignorer les mises à jour test",
        "  f s F3            Source test / Tri test / Mode de recherche test",
        "",
        "Dialogues test:            Tab navigue, Enter confirme, Esc ferme",
        "Choix test:      ← → change le choix, Enter liste les valeurs",
    ];

    [Test]
    public async Task Help_LocalizesEveryKeyboardLineAndSectionWithoutChangingKeyTokens()
    {
        await ResetAsync(TuiPageIds.Installed);
        var provider = BundledAssets.Provider;
        string locale = LanguageEngine.SelectedLocale;
        string preference = Settings.GetValue(Settings.K.PreferredLanguage);
        // "default" is an existing, recognized LanguageReference key without a disk asset.
        // This lets the existing disk-first provider load a fully isolated resource without
        // modifying output locales, production translators, or the language-reference cache.
        const string resource = "Languages/lang_default.json";
        NAssert.That(LanguageData.LanguageReference.ContainsKey("default"), Is.True);
        NAssert.That(File.Exists(BundledAssets.DiskPath(resource)), Is.False);
        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Translations));
        try
        {
            await OnUi(() =>
            {
                BundledAssets.Provider = path => path == resource ? new MemoryStream(json, writable: false) : provider?.Invoke(path);
                CoreTools.ReloadLanguageEngineInstance("default");
                NAssert.That(HelpDialog.GetKeyboardReference(), Is.EqualTo(ExpectedReference));
            });
            await Key(K.F1);
            await WaitForDialogAsync("Aide test");
            await WaitForTextAsync("Raccourcis test", "Fermer test");
            var frames = new List<string> { await ScreenAsync() };
            for (int i = 0; i < 5; i++)
            {
                await Key(K.PageDown);
                await UITest.WaitRendered();
                frames.Add(await ScreenAsync());
            }
            var renderedLines = frames.SelectMany(frame => frame.Split('\n')).Select(Normalize).ToArray();
            foreach (string expected in ExpectedReference.Where(line => line.Length > 0))
                NAssert.That(renderedLines.Any(line => line.Contains(Normalize(expected), StringComparison.Ordinal)),
                    Is.True, "Missing translated/key-preserving rendered line: " + expected);
            foreach (string expected in new[]
            {
                "Documentation test FR", "Ouvrir aide test", "Référence CLI test", "Notes de version test",
                "Signaler un problème test", "Vérifier les mises à jour test",
                "Installer les mises à jour avec l'application test", "Ligne de commande test", "Thèmes test (--theme <id>):",
            })
                NAssert.That(frames.Any(frame => frame.Contains(expected, StringComparison.Ordinal)), Is.True, expected);
            NAssert.That(Settings.GetValue(Settings.K.PreferredLanguage), Is.EqualTo(preference));
            await Key(K.Escape);
            await WaitForNoDialogAsync();
            NAssert.That(await OnUi(() => Window.CurrentPageId), Is.EqualTo(TuiPageIds.Installed));
        }
        finally
        {
            await OnUi(() =>
            {
                TuiModal.CloseAll();
                BundledAssets.Provider = provider;
                CoreTools.ReloadLanguageEngineInstance(locale);
                LanguageEngine.SelectedLocale = locale;
                Window.ResetPages();
                Window.NavigateTo(TuiPageIds.Installed);
            });
        }
    }

    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();
}
