using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UniGetUI.Controls.OperationWidgets;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.Pages.DialogPages;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace UniGetUI.Interface.Widgets
{
    public class SourceItem
    {
        public SourceManager Parent;
        public IManagerSource Source;

        public SourceItem(SourceManager Parent, IManagerSource Source)
        {
            this.Parent = Parent;
            this.Source = Source;
        }

        public void Remove(object sender, RoutedEventArgs e)
        {
            var op = MainApp.Operations.Add(new RemoveSourceOperation(Source));
            op.Operation.OperationSucceeded += (_, _) =>
            {
                Parent.RemoveSourceItem(this);
            };
        }
    }

    public sealed partial class SourceManager : UserControl
    {
        private IPackageManager Manager { get; set; }

        // ReSharper disable once FieldCanBeMadeReadOnly.Local
        private ObservableCollection<SourceItem> Sources = [];

        private ListView _datagrid { get; set; }

        public SourceManager(IPackageManager Manager)
        {
            this.Manager = Manager;
            InitializeComponent();

            if (!Manager.Capabilities.SupportsCustomSources)
            {
                throw new InvalidOperationException(
                    $"Attempted to create a SourceManager class from Manager {Manager.Name}, which does not support custom sources"
                );
            }

            Header.Text = CoreTools.Translate("Manage {0} sources", Manager.DisplayName);
            AddSourceButton.Content = CoreTools.Translate("Add source");
            AddSourceButton.Click += async (sender, e) =>
            {
                try
                {
                    ContentDialog d = new() { Title = CoreTools.Translate("Add source") };

                    ComboBox SourcesCombo = new();
                    Dictionary<string, IManagerSource> NameSourceRef = [];
                    foreach (IManagerSource source in Manager.Properties.KnownSources)
                    {
                        SourcesCombo.Items.Add(source.Name);
                        NameSourceRef.Add(source.Name, source);
                    }

                    d.Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style;
                    StackPanel p = new() { Spacing = 8 };
                    p.Children.Add(
                        new TextBlock
                        {
                            Text = CoreTools.Translate("Select the source you want to add:"),
                        }
                    );
                    p.Children.Add(SourcesCombo);

                    TextBox SourceNameTextBox = new()
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Width = 400,
                    };
                    TextBox SourceUrlTextBox = new()
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                    };

                    StackPanel p1 = new()
                    {
                        Spacing = 2,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                    };
                    p1.Children.Add(
                        new TextBlock
                        {
                            Text = CoreTools.Translate("Source name:"),
                            VerticalAlignment = VerticalAlignment.Center,
                        }
                    );
                    p1.Children.Add(SourceNameTextBox);

                    StackPanel p2 = new()
                    {
                        Spacing = 2,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                    };
                    p2.Children.Add(
                        new TextBlock
                        {
                            Text = CoreTools.Translate("Source URL:"),
                            VerticalAlignment = VerticalAlignment.Center,
                        }
                    );
                    p2.Children.Add(SourceUrlTextBox);

                    p.Children.Add(p1);
                    p.Children.Add(p2);

                    SourcesCombo.Items.Add(CoreTools.Translate("Other"));
                    SourcesCombo.HorizontalAlignment = HorizontalAlignment.Stretch;
                    SourcesCombo.SelectionChanged += (_, _) =>
                    {
                        if (SourcesCombo.SelectedValue.ToString() == CoreTools.Translate("Other"))
                        {
                            SourceUrlTextBox.IsEnabled = SourceNameTextBox.IsEnabled = true;
                            SourceUrlTextBox.Text = SourceNameTextBox.Text = "";
                        }
                        else
                        {
                            string? sourceName = SourcesCombo.SelectedValue.ToString();
                            if (sourceName is not null)
                            {
                                SourceUrlTextBox.IsEnabled = SourceNameTextBox.IsEnabled = false;
                                SourceUrlTextBox.Text = NameSourceRef[sourceName].Url.ToString();
                                SourceNameTextBox.Text = NameSourceRef[sourceName].Name;
                            }
                            else
                            {
                                Logger.Warn(
                                    "SourcesCombo.SelectedValue.ToString() was null on SourceManager.SourceManager"
                                );
                            }
                        }
                    };
                    SourcesCombo.SelectedIndex = 0;

                    d.XamlRoot = XamlRoot;
                    d.Content = p;
                    d.PrimaryButtonText = CoreTools.Translate("Add");
                    d.SecondaryButtonText = CoreTools.Translate("Cancel");
                    d.DefaultButton = ContentDialogButton.Primary;

                    if (await DialogHelper.ShowDialogAsync(d) == ContentDialogResult.Primary)
                    {
                        PackageOperations.AbstractOperation op;
                        if (CoreTools.Translate("Other") != SourcesCombo.SelectedValue.ToString())
                            op = new AddSourceOperation(
                                NameSourceRef[SourcesCombo.SelectedValue.ToString() ?? ""]
                            );
                        else
                            op = new AddSourceOperation(
                                new ManagerSource(
                                    this.Manager,
                                    SourceNameTextBox.Text,
                                    new Uri(SourceUrlTextBox.Text)
                                )
                            );

                        MainApp.Operations.Add(op);
                        op.OperationSucceeded += (_, _) => _ = LoadSources();
                    }
                }
                catch (Exception ex)
                {
                    ContentDialog d = new()
                    {
                        XamlRoot = XamlRoot,
                        Title = CoreTools.Translate("An error occurred"),
                        Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
                        Content =
                            CoreTools.Translate("An error occurred when adding the source: ")
                            + ex.Message,
                    };
                    _ = DialogHelper.ShowDialogAsync(d, HighPriority: true);
                    d.PrimaryButtonText = CoreTools.Translate("Close");
                    Logger.Error("An error occurred when adding the source");
                    Logger.Error(ex);
                }
            };
            this.Manager = Manager;
            _datagrid = DataList;
            DataList.ItemTemplate = (DataTemplate)Resources["ManagerSourceTemplate"];
            _ = LoadSources();
        }

        public async Task LoadSources()
        {
            if (!Manager.IsReady())
            {
                return;
            }

            LoadingBar.Visibility = Visibility.Visible;
            Sources.Clear();
            foreach (IManagerSource source in await Task.Run(Manager.SourcesHelper.GetSources))
            {
                Sources.Add(new SourceItem(this, source));
            }

            if (Sources.Count > 0)
            {
                _datagrid.SelectedIndex = 0;
            }

            LoadingBar.Visibility = Visibility.Collapsed;
        }

        public void RemoveSourceItem(SourceItem Item)
        {
            Sources.Remove(Item);
        }

        private void ReloadButton_Click(object sender, RoutedEventArgs e) => _ = LoadSources();
    }
}
