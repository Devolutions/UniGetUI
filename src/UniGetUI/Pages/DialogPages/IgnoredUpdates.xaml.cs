using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.WingetManager;
using UniGetUI.PackageEngine.PackageLoader;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace UniGetUI.Interface
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class IgnoredUpdatesManager : Page
    {
        public event EventHandler? Close;
        private readonly ObservableCollection<IgnoredPackageEntry> ignoredPackages = [];

        public IgnoredUpdatesManager()
        {
            UpdateData();
            InitializeComponent();
            IgnoredUpdatesList.ItemsSource = ignoredPackages;
        }

        private void UpdateData()
        {
            Dictionary<string, IPackageManager> ManagerNameReference = [];

            foreach (IPackageManager Manager in PEInterface.Managers)
            {
                ManagerNameReference.Add(Manager.Name.ToLower(), Manager);
            }

            ignoredPackages.Clear();

            var rawIgnoredPackages = IgnoredUpdatesDatabase.GetDatabase();

            foreach (var (ignoredId, version) in rawIgnoredPackages)
            {
                IPackageManager? manager = PEInterface.WinGet; // Manager by default
                if (ManagerNameReference.ContainsKey(ignoredId.Split("\\")[0]))
                {
                    manager = ManagerNameReference[ignoredId.Split("\\")[0]];
                }

                if (manager is null)
                    continue;

                ignoredPackages.Add(
                    new IgnoredPackageEntry(
                        ignoredId.Split("\\")[^1],
                        version,
                        manager,
                        ignoredPackages
                    )
                );
            }
        }

        /*private async void IgnoredUpdatesList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (IgnoredUpdatesList.SelectedItem is IgnoredPackageEntry package)
            {
                await package.RemoveFromIgnoredUpdates();
            }
        }*/

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close?.Invoke(this, EventArgs.Empty);
        }

        private void YesResetButton_Click(object sender, RoutedEventArgs e) =>
            _ = _yesResetButton_Click();

        private async Task _yesResetButton_Click()
        {
            foreach (IgnoredPackageEntry package in ignoredPackages.ToArray())
            {
                await package.RemoveFromIgnoredUpdatesAsync();
            }
            ConfirmResetFlyout.Hide();
        }

        private void NoResetButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmResetFlyout.Hide();
        }
    }

    public class IgnoredPackageEntry
    {
        public string Id { get; }
        public string Name { get; }
        public string Version { get; }
        public string NewVersion { get; }
        public IPackageManager Manager { get; }
        private ObservableCollection<IgnoredPackageEntry> List { get; }

        public IgnoredPackageEntry(
            string id,
            string version,
            IPackageManager manager,
            ObservableCollection<IgnoredPackageEntry> list
        )
        {
            Id = id;

            if (manager is WinGet && id.Contains('.'))
                Name = String.Join(' ', id.Split('.')[1..]);
            else
                Name = CoreTools.FormatAsName(id);

            if (version == "*")
            {
                Version = CoreTools.Translate("All versions");
            }
            else
            {
                Version = version;
            }

            string CurrentVersion =
                InstalledPackagesLoader.Instance.GetPackageForId(id)?.VersionString ?? "Unknown";

            if (
                UpgradablePackagesLoader.Instance.IgnoredPackages.TryGetValue(
                    Id,
                    out IPackage? package
                )
                && package.NewVersionString != package.VersionString
            )
            {
                NewVersion = CurrentVersion + " \u27a4 " + package.NewVersionString;
            }
            else if (CurrentVersion != "Unknown")
            {
                NewVersion = CoreTools.Translate("Up to date") + $" ({CurrentVersion})";
                ;
            }
            else
            {
                NewVersion = CoreTools.Translate("Unknown");
            }

            Manager = manager;
            List = list;
        }

        public void RemoveFromIgnoredUpdates() => _ = RemoveFromIgnoredUpdatesAsync();

        public async Task RemoveFromIgnoredUpdatesAsync()
        {
            string ignoredId = $"{Manager.Properties.Name.ToLower()}\\{Id}";
            await Task.Run(() => IgnoredUpdatesDatabase.Remove(ignoredId));

            // If possible, add the package to the software updates tab again
            if (
                UpgradablePackagesLoader.Instance.IgnoredPackages.TryRemove(
                    Id,
                    out IPackage? nativePackage
                )
                && nativePackage.NewVersionString != nativePackage.VersionString
            )
            {
                await UpgradablePackagesLoader.Instance.AddForeign(nativePackage);
            }

            foreach (IPackage package in InstalledPackagesLoader.Instance.Packages)
            {
                if (Manager == package.Manager && package.Id == Id)
                {
                    package.SetTag(PackageTag.Default);
                    break;
                }
            }

            List.Remove(this);
        }
    }
}
