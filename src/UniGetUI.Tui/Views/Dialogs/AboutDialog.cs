using System.Runtime.InteropServices;
using Avalonia;
using Consolonia;
using UniGetUI.Core.Data;
using UniGetUI.Core.Language;
using UniGetUI.Core.Tools;
using UniGetUI.Tui.FakeData;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Theme;
using UniGetUI.Tui.Views.Controls;
using UniGetUI.Tui.Views.Pages;

namespace UniGetUI.Tui.Views.Dialogs;

/// <summary>About (desktop About window): version, links, terminal diagnostics, contributors, translators and
/// third-party licenses, in a scrollable dialog over the current page.</summary>
internal sealed class AboutDialog : TuiDialog
{
    private readonly TuiForm _form = new();

    private AboutDialog()
        : base(CoreTools.Translate("About UniGetUI TUI"))
    {
        SetFrameWidth(96);
        SetFrameHeight(32);
        Body.Content = _form;

        _form.AddHeader("UniGetUI");
        _form.AddNote($"{CoreTools.Translate("Version")}: {CoreData.VersionName} (build {CoreData.BuildNumber})");
        _form.AddNote(CoreTools.Translate("The main goal of this project is to create an intuitive UI to manage the most common CLI package managers for Windows, such as Winget and Scoop."));
        _form.AddNote(CoreTools.Translate("UniGetUI is not related to the compatible package managers. UniGetUI is an independent project."));
        _form.AddButton("https://devolutions.net/unigetui", () => TuiPackageActions.OpenExternally("https://devolutions.net/unigetui"));
        _form.AddButton(HelpDialog.HelpUrl, () => TuiPackageActions.OpenExternally(HelpDialog.HelpUrl));
        _form.AddButton(CoreTools.Translate("Report an issue or submit a feature request"), () => TuiPackageActions.OpenExternally(HelpDialog.IssuesUrl));

        _form.AddHeader(CoreTools.Translate("Terminal Diagnostics"));
        _form.AddNote("UI: Consolonia (Avalonia 12 on the terminal)");
        _form.AddNote($"Runtime: {RuntimeInformation.FrameworkDescription}");
        _form.AddNote($"OS: {RuntimeInformation.OSDescription}  ({RuntimeInformation.ProcessArchitecture})");
        _form.AddNote("Theme: " + Safe(ThemeDescription));
        _form.AddNote("Console: " + Safe(() => $"{ConsoloniaLifetime.Console.Size.Width}x{ConsoloniaLifetime.Console.Size.Height} cells"));
        _form.AddNote("Capabilities: " + Safe(ConsoloniaLifetime.Console.Capabilities.ToString));
        _form.AddNote("Terminal: " + TerminalDescription());
        if (FakeDataEnvironment.Current is { } env)
            _form.AddNote($"FAKE DATA MODE: sandbox {env.SandboxDirectory}", TuiPalette.Error);

        _form.AddHeader(CoreTools.Translate("Contributors"));
        _form.AddNote(Safe(() => string.Join(", ", ContributorsData.Contributors)));
        _form.AddHeader(CoreTools.Translate("Translators"));
        _form.AddNote(Safe(() => string.Join(", ", LanguageData.TranslatorsList.Select(p => $"{p.Name} ({p.Language})"))));
        _form.AddHeader(CoreTools.Translate("Third-party licenses"));
        foreach (var (name, license) in LicenseData.LicenseNames.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            _form.AddNote($"{name}: {license}" + (LicenseData.HomepageUrls.TryGetValue(name, out var url) ? $"  {url}" : ""));

        AddButton(CoreTools.Translate("Close"), () => Close(null));
    }

    public static Task ShowAsync() => TuiModal.ShowAsync(new AboutDialog());

    public override void FocusInitial()
    {
        if (!_form.FocusPrimary()) base.FocusInitial();
    }

    private static string ThemeDescription()
    {
        bool rgb = Application.Current?.ApplicationLifetime is ConsoloniaLifetime lifetime && lifetime.IsRgbColorMode();
        return $"{TuiPalette.Current.Name} ({(rgb ? "truecolor RGB" : "16-colour console palette")})";
    }

    private static string TerminalDescription()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WT_SESSION"))) return "Windows Terminal";
        return Environment.GetEnvironmentVariable("TERM_PROGRAM") ?? Environment.GetEnvironmentVariable("TERM") ?? "not reported";
    }

    private static string Safe(Func<string> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return "unavailable";
        }
    }
}
