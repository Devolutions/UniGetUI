using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Avalonia.Views.Pages.ManagersPages;

public partial class ManagersHomeView : UserControl, IManagerSectionView
{
    private TextBlock IntroTitleText => GetControl<TextBlock>("IntroTitleBlock");

    private TextBlock IntroDescriptionText => GetControl<TextBlock>("IntroDescriptionBlock");

    private StackPanel RowsHost => GetControl<StackPanel>("RowsHostPanel");

    public ManagersHomeView()
    {
        InitializeComponent();

        IntroTitleText.Text = CoreTools.Translate("Package manager preferences");
        IntroDescriptionText.Text = CoreTools.Translate("Enable and disable package managers, change default install options, etc.");

        foreach (var manager in PEInterface.Managers)
        {
            try
            {
                var row = new ManagerRowView();
                row.LoadManager(manager);
                row.NavigationRequested += Row_NavigationRequested;
                RowsHost.Children.Add(row);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to build manager overview row for {manager.Name}");
                Logger.Error(ex);
                RowsHost.Children.Add(CreateFallbackRow(manager));
            }
        }
    }

    internal event EventHandler<IPackageManager>? NavigationRequested;

    public string SectionTitle => CoreTools.Translate("Package manager preferences");

    public string SectionSubtitle => CoreTools.Translate("Enable and disable package managers, change default install options, etc.");

    public string SectionStatus => CoreTools.Translate("{0} settings", PEInterface.Managers.Length);

    private void Row_NavigationRequested(object? sender, IPackageManager manager)
    {
        NavigationRequested?.Invoke(this, manager);
    }

    private static Border CreateFallbackRow(IPackageManager manager)
    {
        return new Border
        {
            Classes = { "surface-card", "subtle-panel" },
            Padding = new global::Avalonia.Thickness(18),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        FontSize = 16,
                        FontWeight = global::Avalonia.Media.FontWeight.SemiBold,
                        Text = manager.DisplayName,
                    },
                    new TextBlock
                    {
                        Opacity = 0.78,
                        TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                        Text = CoreTools.Translate("Something went wrong"),
                    },
                },
            },
        };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private T GetControl<T>(string name)
        where T : Control
    {
        return this.FindControl<T>(name)
            ?? throw new InvalidOperationException($"Control '{name}' was not found.");
    }
}
