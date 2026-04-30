using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using UniGetUI.Core.Classes;
using UniGetUI.Core.Tools;
using UniGetUI.Interface;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.PackageClasses
{
    /// <summary>
    /// A wrapper for packages to be able to show in ItemCollections
    /// </summary>
    public partial class PackageWrapper : IIndexableListItem, INotifyPropertyChanged, IDisposable
    {
        private static readonly ConcurrentDictionary<long, Uri?> CachedPackageIcons = new();

        public static void ResetIconCache()
        {
            CachedPackageIcons.Clear();
        }

        public bool IsChecked
        {
            get => Package.IsChecked;
            set
            {
                Package.IsChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CheckedStatus)));
                _page.UpdatePackageCount();
            }
        }

        public string CheckedStatus => IsChecked ? CoreTools.Translate("Checked") : CoreTools.Translate("Unchecked");

        public bool IconWasLoaded;
        public bool AlternateIdIconVisible;
        public bool ShowCustomPackageIcon;
        public bool ShowDefaultPackageIcon = true;
        public string VersionComboString;
        public IconType MainIconId = IconType.Id;
        public IconType AlternateIconId = IconType.Id;
        public ImageSource? MainIconSource;

        public Uri? PackageIcon
        {
            set
            {
                CachedPackageIcons[Package.GetHash()] = value;
                UpdatePackageIcon();
            }
        }

        public string ListedNameTooltip = "";
        public readonly string ExtendedTooltip = "";
        public float ListedOpacity = 1.0f;

        public int NewVersionLabelWidth
        {
            get => Package.IsUpgradable ? 125 : 0;
        }
        public int NewVersionIconWidth
        {
            get => Package.IsUpgradable ? 24 : 0;
        }

        public int Index { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;

        public IPackage Package { get; private set; }
        public PackageWrapper Self { get; private set; }

        private readonly AbstractPackagesPage _page;

        public PackageWrapper(IPackage package, AbstractPackagesPage page)
        {
            Package = package;
            Self = this;
            _page = page;
            WhenTagHasChanged();
            Package.PropertyChanged += Package_PropertyChanged;
            UpdatePackageIcon();
            VersionComboString = package.IsUpgradable
                ? $"{package.VersionString} -> {package.NewVersionString}"
                : package.VersionString;

            if (package.Name.ToLower() != package.Id.ToLower())
                ExtendedTooltip =
                    $"{package.Name} ({package.Id} from {package.Source.AsString_DisplayName})";
            else
                ExtendedTooltip = $"{package.Name} (from {package.Source.AsString_DisplayName})";
        }

        public void PackageItemContainer_DoubleTapped(
            object sender,
            DoubleTappedRoutedEventArgs e
        ) => _page.PackageItemContainer_DoubleTapped(sender, e);

        public void PackageItemContainer_PreviewKeyDown(object sender, KeyRoutedEventArgs e) =>
            _page.PackageItemContainer_PreviewKeyDown(sender, e);

        public void PackageItemContainer_RightTapped(object sender, RightTappedRoutedEventArgs e) =>
            _page.PackageItemContainer_RightTapped(sender, e);

        public void RightClick() => _ = RightClickAsync();

        private async Task RightClickAsync()
        {
            await _page.ShowContextMenu(this);
        }

        public void Package_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            try
            {
                if (e.PropertyName == nameof(Package.Tag))
                {
                    WhenTagHasChanged();
                    PropertyChanged?.Invoke(
                        this,
                        new PropertyChangedEventArgs(nameof(ListedOpacity))
                    );
                    PropertyChanged?.Invoke(
                        this,
                        new PropertyChangedEventArgs(nameof(AlternateIconId))
                    );
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MainIconId)));
                    PropertyChanged?.Invoke(
                        this,
                        new PropertyChangedEventArgs(nameof(AlternateIdIconVisible))
                    );
                    PropertyChanged?.Invoke(
                        this,
                        new PropertyChangedEventArgs(nameof(ListedNameTooltip))
                    );
                }
                else if (e.PropertyName == nameof(Package.IsChecked))
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                }
                else
                {
                    PropertyChanged?.Invoke(this, e);
                }
            }
            catch (COMException)
            {
                // ignore
            }
        }

        public void Dispose()
        {
            Package.PropertyChanged -= Package_PropertyChanged;
        }

        /// <summary>
        /// Updates the fields that change how the item template is rendered.
        /// </summary>
        public void WhenTagHasChanged()
        {
            MainIconId = Package.Tag switch
            {
                PackageTag.Default => IconType.Id,
                PackageTag.AlreadyInstalled => IconType.Installed,
                PackageTag.IsUpgradable => IconType.Upgradable,
                PackageTag.Pinned => IconType.Pin,
                PackageTag.OnQueue => IconType.SandClock,
                PackageTag.BeingProcessed => IconType.Loading,
                PackageTag.Failed => IconType.Warning,
                PackageTag.Unavailable => IconType.Help,
                _ => throw new ArgumentException($"Unknown tag {Package.Tag}"),
            };

            AlternateIconId = Package.Tag switch
            {
                PackageTag.Default => IconType.Empty,
                PackageTag.AlreadyInstalled => IconType.Installed_Filled,
                PackageTag.IsUpgradable => IconType.Upgradable_Filled,
                PackageTag.Pinned => IconType.Pin_Filled,
                PackageTag.OnQueue => IconType.Empty,
                PackageTag.BeingProcessed => IconType.Loading_Filled,
                PackageTag.Failed => IconType.Warning_Filled,
                PackageTag.Unavailable => IconType.Empty,
                _ => throw new ArgumentException($"Unknown tag {Package.Tag}"),
            };
            AlternateIdIconVisible = AlternateIconId != IconType.Empty;

            ListedNameTooltip =
                Package.Tag switch
                {
                    PackageTag.Default => "",
                    PackageTag.AlreadyInstalled => CoreTools.Translate(
                        "This package is already installed"
                    ) + " - ",
                    PackageTag.IsUpgradable => CoreTools.Translate(
                        "This package can be upgraded to version {0}",
                        Package.GetUpgradablePackage()?.NewVersionString ?? "-1"
                    ) + " - ",
                    PackageTag.Pinned => CoreTools.Translate("Updates for this package are ignored")
                        + " - ",
                    PackageTag.OnQueue => CoreTools.Translate(
                        "This package is on the queue" + " - "
                    ),
                    PackageTag.BeingProcessed => CoreTools.Translate(
                        "This package is being processed"
                    ) + " - ",
                    PackageTag.Failed => CoreTools.Translate(
                        "An error occurred while processing this package"
                    ) + " - ",
                    PackageTag.Unavailable => CoreTools.Translate("This package is not available")
                        + " - ",
                    _ => throw new ArgumentException($"Unknown tag {Package.Tag}"),
                } + Package.Name;

            ListedOpacity = Package.Tag switch
            {
                PackageTag.Default => 1,
                PackageTag.AlreadyInstalled => 1,
                PackageTag.IsUpgradable => 1,
                PackageTag.Pinned => 1,
                PackageTag.OnQueue => .5F,
                PackageTag.BeingProcessed => .5F,
                PackageTag.Failed => 1,
                PackageTag.Unavailable => .5F,
                _ => throw new ArgumentException($"Unknown tag {Package.Tag}"),
            };
#pragma warning restore CS8524
        }

        public void UpdatePackageIcon()
        {
            if (CachedPackageIcons.TryGetValue(Package.GetHash(), out Uri? icon))
            {
                MainIconSource = new BitmapImage
                {
                    UriSource = icon,
                    DecodePixelWidth = 64,
                    DecodePixelType = DecodePixelType.Logical,
                };
                ShowCustomPackageIcon = true;
                ShowDefaultPackageIcon = false;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MainIconSource)));
            }
            else
            {
                ShowCustomPackageIcon = false;
                ShowDefaultPackageIcon = true;
            }
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(ShowCustomPackageIcon))
            );
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(ShowDefaultPackageIcon))
            );
        }
    }
}
