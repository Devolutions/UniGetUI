using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Consolonia;
using Consolonia.Controls;
using UniGetUI.Core.Data;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Theme;

namespace UniGetUI.Tui.Views.Pages;

/// <summary>
/// Builders for the TUI's page content. M1 ships the navigation shell with mostly descriptive
/// placeholders; the Managers and About pages already render real engine/runtime state to prove the
/// Consolonia front-end is correctly wired to the UniGetUI package engine. M2+ replaces the
/// placeholders with the real Discover/Installed/Updates/Bundles/Settings experiences.
/// </summary>
internal static class TuiPages
{
    public static Control Build(string pageId) => pageId switch
    {
        "discover" => new PackageListPage(PackagePageKind.Discover),
        "installed" => new PackageListPage(PackagePageKind.Installed),
        "updates" => new PackageListPage(PackagePageKind.Updates),
        "operations" => new OperationsPage(),
        "bundles" => new BundlesPage(),
        "managers" => BuildManagersPage(),
        "settings" => new SettingsPage(),
        "logs" => new LogsPage(),
        "about" => BuildAboutPage(),
        _ => Placeholder("UniGetUI", "Unknown page.", pageId),
    };

    private static Control Placeholder(string title, string subtitle, string note)
    {
        var panel = new StackPanel { Margin = new Thickness(2, 1, 2, 1), Spacing = 1 };
        panel.Children.Add(Heading(title));
        panel.Children.Add(new TextBlock { Text = subtitle, TextWrapping = TextWrapping.Wrap, });
        panel.Children.Add(new TextBlock
        {
            Text = note,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#C0C0C0")),
            Margin = new Thickness(0, 1, 0, 0),
        });
        return panel;
    }

    private static Control BuildManagersPage()
    {
        var panel = new StackPanel { Margin = new Thickness(2, 1, 2, 1), Spacing = 0 };
        panel.Children.Add(Heading("Package Managers"));
        panel.Children.Add(TuiChrome.Separator());

        IReadOnlyList<IPackageManager> managers = PEInterface.Managers;
        int ready = 0;

        // Header row
        panel.Children.Add(ManagerRow("Manager", "Version", "Status", isHeader: true));

        foreach (var manager in managers)
        {
            bool isReady = SafeIsReady(manager);
            if (isReady) ready++;

            string status = !SafeIsEnabled(manager) ? "disabled"
                : !manager.Status.Found ? "not found"
                : isReady ? "ready"
                : "unavailable";

            string version = string.IsNullOrWhiteSpace(manager.Status.Version)
                ? "—"
                : manager.Status.Version.Split('\n')[0].Trim();

            panel.Children.Add(ManagerRow(manager.Name, version, status, isHeader: false, isReady: isReady));
        }

        panel.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 1, 0, 0),
            Text = $"{ready} of {managers.Count} managers ready on this system.",
            Foreground = DevolutionsPalette.BrandBrush,
        });

        AppendSourcesSection(panel, managers);

        return new ScrollablePage(panel);
    }

    private static void AppendSourcesSection(StackPanel panel, IReadOnlyList<IPackageManager> managers)
    {
        panel.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 2, 0, 1),
            Text = "Package Sources",
            Foreground = DevolutionsPalette.BrandBrush,
            FontWeight = FontWeight.Bold,
        });

        bool any = false;
        foreach (var manager in managers)
        {
            if (!SafeIsReady(manager)) continue;
            if (manager.Capabilities.SupportsCustomSources != true) continue;

            IReadOnlyList<IManagerSource> sources;
            try
            {
                sources = manager.SourcesHelper.GetSources();
            }
            catch
            {
                continue;
            }

            if (sources.Count == 0) continue;

            any = true;
            panel.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 1, 0, 0),
                Text = manager.Name,
                Foreground = new SolidColorBrush(Color.Parse("#C0C0C0")),
                FontWeight = FontWeight.Bold,
            });

            foreach (var source in sources)
            {
                string url = SafeSourceUrl(source);
                panel.Children.Add(new TextBlock
                {
                    Text = url.Length > 0 ? $"    • {source.Name}  —  {url}" : $"    • {source.Name}",
                    Foreground = new SolidColorBrush(Color.Parse("#C0C0C0")),
                });
            }
        }

        if (!any)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No custom-source managers are currently available.",
                Foreground = new SolidColorBrush(Color.Parse("#C0C0C0")),
            });
        }
    }

    private static string SafeSourceUrl(IManagerSource source)
    {
        try
        {
            return source.Url?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static Control ManagerRow(string name, string version, string status, bool isHeader, bool isReady = false)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("24,16,*"), Margin = new Thickness(0, 0, 0, 0),
        };

        var nameBlock = new TextBlock { Text = Fit(name, 22) };
        var versionBlock = new TextBlock { Text = Fit(version, 14) };
        var statusBlock = new TextBlock { Text = status };

        if (isHeader)
        {
            nameBlock.Foreground = versionBlock.Foreground = statusBlock.Foreground = DevolutionsPalette.BrandBrush;
        }
        else
        {
            statusBlock.Foreground =
                isReady ? DevolutionsPalette.SuccessTextBrush : new SolidColorBrush(Color.Parse("#C0C0C0"));
        }

        Grid.SetColumn(nameBlock, 0);
        Grid.SetColumn(versionBlock, 1);
        Grid.SetColumn(statusBlock, 2);
        grid.Children.Add(nameBlock);
        grid.Children.Add(versionBlock);
        grid.Children.Add(statusBlock);
        return grid;
    }

    private static Control BuildAboutPage()
    {
        var panel = new StackPanel { Margin = new Thickness(2, 1, 2, 1), Spacing = 0 };
        panel.Children.Add(Heading("About UniGetUI TUI"));
        panel.Children.Add(TuiChrome.Separator());

        void Line(string label, string value)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*") };
            var l = new TextBlock { Text = label, Foreground = DevolutionsPalette.BrandBrush };
            var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(l, 0);
            Grid.SetColumn(v, 1);
            grid.Children.Add(l);
            grid.Children.Add(v);
            panel.Children.Add(grid);
        }

        Line("Version", $"{CoreData.VersionName} (build {CoreData.BuildNumber})");
        Line("UI", "Consolonia (Avalonia 12 on the terminal)");
        Line("Runtime", RuntimeInformation.FrameworkDescription);
        Line("OS", RuntimeInformation.OSDescription);
        Line("Arch", $"{RuntimeInformation.ProcessArchitecture} (OS: {RuntimeInformation.OSArchitecture})");

        panel.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 2, 0, 1),
            Text = "Terminal Diagnostics",
            Foreground = DevolutionsPalette.BrandBrush,
            FontWeight = FontWeight.Bold,
        });
        Line("Theme", SafeDiagnostic(ThemeDescription));
        Line("Console", SafeDiagnostic(ConsoleSizeDescription));
        Line("Capabilities", SafeDiagnostic(ConsoleCapabilitiesDescription));
        Line("Terminal", TerminalDescription());

        panel.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 1, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#C0C0C0")),
            Text = "A terminal front-end for UniGetUI, styled with the Devolutions palette. "
                   + "Full feature parity with the desktop app is being built milestone by milestone.",
        });

        return new ScrollablePage(panel);
    }

    private static string ThemeDescription()
    {
        bool rgb = Application.Current?.ApplicationLifetime is ConsoloniaLifetime lifetime
                   && lifetime.IsRgbColorMode();
        return rgb ? "Modern dark, truecolor RGB" : "TurboVision dark, ANSI palette fallback";
    }

    private static string ConsoleSizeDescription()
    {
        var size = ConsoloniaLifetime.Console.Size;
        return $"{size.Width}x{size.Height} cells";
    }

    private static string ConsoleCapabilitiesDescription()
    {
        ConsoleCapabilities capabilities = ConsoloniaLifetime.Console.Capabilities;
        return capabilities == ConsoleCapabilities.None ? "none reported" : capabilities.ToString();
    }

    private static string TerminalDescription()
    {
        string? wt = Environment.GetEnvironmentVariable("WT_SESSION");
        if (!string.IsNullOrWhiteSpace(wt)) return "Windows Terminal";

        string? termProgram = Environment.GetEnvironmentVariable("TERM_PROGRAM");
        string? term = Environment.GetEnvironmentVariable("TERM");
        if (!string.IsNullOrWhiteSpace(termProgram)) return termProgram;
        if (!string.IsNullOrWhiteSpace(term)) return term;
        return "not reported";
    }

    private static string SafeDiagnostic(Func<string> getter)
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

    private static Control Heading(string text) => TuiChrome.PageTitle("*", text);

    private static string Fit(string value, int width)
    {
        value ??= string.Empty;
        if (value.Length <= width) return value;
        return width <= 1 ? value[..width] : value[..(width - 1)] + "…";
    }

    private static bool SafeIsReady(IPackageManager m)
    {
        try
        {
            return m.IsReady();
        }
        catch
        {
            return false;
        }
    }

    private static bool SafeIsEnabled(IPackageManager m)
    {
        try
        {
            return m.IsEnabled();
        }
        catch
        {
            return false;
        }
    }
}
