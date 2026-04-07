using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.WinUI;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.Interface.Pages;
using UniGetUI.Interface.Telemetry;
using UniGetUI.Interface.Widgets;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.Pages.DialogPages;
using UniGetUI.Pages.PageInterfaces;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace UniGetUI.Interface
{
    public abstract partial class AbstractPackagesPage
        : IKeyboardShortcutListener,
            IEnterLeaveListener,
            ISearchBoxPage
    {
        protected struct PackagesPageData
        {
            public bool DisableAutomaticPackageLoadOnStart;
            public bool MegaQueryBlockEnabled;
            public bool PackagesAreCheckedByDefault;
            public bool ShowLastLoadTime;
            public bool DisableSuggestedResultsRadio;
            public bool DisableFilterOnQueryChange;
            public bool DisableReload;

            public OperationType PageRole;
            public AbstractPackageLoader Loader;

            public string PageName;
            public string PageTitle;
            public string Glyph;

            public string NoPackages_BackgroundText;
            public string NoPackages_SourcesText;
            public string NoPackages_SubtitleText_Base;
            public string MainSubtitle_StillLoading;
            public string NoMatches_BackgroundText;
        }

        protected enum ReloadReason
        {
            FirstRun,
            Automated,
            Manual,
            External,
        }

        static class FilterHelpers
        {
            public static string NormalizeCase(string input) => input.ToLower();

            public static string NormalizeSpecialCharacters(string input)
            {
                input = input
                    .Replace("-", "")
                    .Replace("_", "")
                    .Replace(" ", "")
                    .Replace("@", "")
                    .Replace("\t", "")
                    .Replace(".", "")
                    .Replace(",", "")
                    .Replace(":", "");
                foreach (
                    KeyValuePair<char, string> entry in new Dictionary<char, string>
                    {
                        { 'a', "àáäâ" },
                        { 'e', "èéëê" },
                        { 'i', "ìíïî" },
                        { 'o', "òóöô" },
                        { 'u', "ùúüû" },
                        { 'y', "ýÿ" },
                        { 'c', "ç" },
                        { 'ñ', "n" },
                    }
                )
                {
                    foreach (char InvalidChar in entry.Value)
                    {
                        input = input.Replace(InvalidChar, entry.Key);
                    }
                }
                return input;
            }

            public static bool NameContains(
                IPackage pkg,
                string query,
                List<Func<string, string>> filters
            )
            {
                string treatedName = pkg.Name;
                foreach (var filter in filters)
                    treatedName = filter(treatedName);
                return treatedName.Contains(query);
            }

            public static bool IdContains(
                IPackage pkg,
                string query,
                List<Func<string, string>> filters
            )
            {
                string treatedId = pkg.Id;
                foreach (var filter in filters)
                    treatedId = filter(treatedId);
                return treatedId.Contains(query);
            }

            public static bool NameOrIdContains(
                IPackage pkg,
                string query,
                List<Func<string, string>> filters
            ) => NameContains(pkg, query, filters) || IdContains(pkg, query, filters);

            public static bool NameOrIdExactMatch(
                IPackage pkg,
                string query,
                List<Func<string, string>> filters
            )
            {
                string treatedId = pkg.Id;
                foreach (var filter in filters)
                    treatedId = filter(treatedId);
                if (query == treatedId)
                    return true;

                string treatedName = pkg.Name;
                foreach (var filter in filters)
                    treatedName = filter(treatedName);
                return query == treatedName;
            }
        }

        protected readonly bool DISABLE_AUTOMATIC_PACKAGE_LOAD_ON_START;
        protected readonly bool MEGA_QUERY_BOX_ENABLED;
        protected readonly bool SHOW_LAST_CHECKED_TIME;
        protected readonly bool DISABLE_FILTER_ON_QUERY_CHANGE;
        protected readonly bool DISABLE_RELOAD;
        protected readonly string PAGE_NAME;
        public readonly bool RoleIsUpdateLike;
        protected DateTime LastPackageLoadTime { get; private set; }
        protected readonly OperationType PAGE_ROLE;

        protected AutoSuggestBox QueryBlock
        {
            get => MainApp.Instance.MainWindow.GlobalSearchBox;
        }

        protected IPackage? SelectedItem
        {
            get
            {
                if (CurrentPackageList.SelectedItem is PackageWrapper w)
                    return w.Package;

                var fp = FilteredPackages.GetCheckedPackages();
                if (fp.Count == 1)
                    return fp.First();
                DialogHelper.ShowDismissableBalloon(
                    CoreTools.Translate("Invalid selection"),
                    fp.Count == 0
                        ? CoreTools.Translate("No package was selected")
                        : CoreTools.Translate("More than 1 package was selected")
                );
                return null;
            }
        }

        protected ItemsView CurrentPackageList
        {
            get =>
                (
                    ViewModeSelector.SelectedIndex switch
                    {
                        1 => PackageList_Grid,
                        2 => PackageList_Icons,
                        _ => PackageList_List,
                    }
                );
        }

        protected AbstractPackageLoader Loader;

        public readonly ObservablePackageCollection FilteredPackages = [];
        private readonly ObservableCollection<PackageWrapper> WrappedPackages = [];
        private IEnumerable<PackageWrapper>? LastQueryResult;

        protected List<IPackageManager> UsedManagers = [];
        protected ConcurrentDictionary<
            IPackageManager,
            List<IManagerSource>
        > UsedSourcesForManager = [];
        protected ConcurrentDictionary<IPackageManager, TreeViewNode> RootNodeForManager = [];
        protected ConcurrentDictionary<IManagerSource, TreeViewNode> NodesForSources = [];
        private readonly TreeViewNode LocalPackagesNode = new();

        public readonly int NewVersionLabelWidth;
        public readonly int NewVersionIconWidth;
        private SplitViewDisplayMode _filterPanelCurrentMode = SplitViewDisplayMode.CompactInline;

        protected abstract void WhenPackagesLoaded(ReloadReason reason);
        protected abstract void WhenPackageCountUpdated();
        protected abstract void WhenShowingContextMenu(IPackage package);
        public abstract void GenerateToolBar();
        public abstract BetterMenu GenerateContextMenu();

        protected readonly string NoPackages_BackgroundText;
        protected readonly string NoPackages_SourcesText;
        protected readonly string MainSubtitle_StillLoading;
        protected readonly string NoPackages_SubtitleText_Base;
        protected readonly string NoMatches_BackgroundText;

        protected string NoPackages_SubtitleText
        {
            get =>
                NoPackages_SubtitleText_Base
                + (
                    SHOW_LAST_CHECKED_TIME
                        ? " "
                            + CoreTools.Translate(
                                "(Last checked: {0})",
                                LastPackageLoadTime.ToString(CultureInfo.CurrentCulture)
                            )
                        : ""
                );
        }

        public string QueryBackup { get; set; } = "";

        private readonly string _searchPlaceholder;
        public string SearchBoxPlaceholder => _searchPlaceholder;

        private string TypeQuery = "";
        private int LastKeyDown;
        private readonly int QUERY_SEPARATION_TIME = 1000; // 500ms between keypresses starts a new query

        protected AbstractPackagesPage(PackagesPageData data)
        {
            // Load page attributes
            PAGE_NAME = data.PageName;
            DISABLE_AUTOMATIC_PACKAGE_LOAD_ON_START = data.DisableAutomaticPackageLoadOnStart;
            DISABLE_FILTER_ON_QUERY_CHANGE = data.DisableFilterOnQueryChange;
            MEGA_QUERY_BOX_ENABLED = data.MegaQueryBlockEnabled;
            SHOW_LAST_CHECKED_TIME = data.ShowLastLoadTime;
            DISABLE_RELOAD = data.DisableReload;

            PAGE_ROLE = data.PageRole;
            RoleIsUpdateLike = PAGE_ROLE == OperationType.Update;
            NewVersionLabelWidth = RoleIsUpdateLike ? 125 : 0;
            NewVersionIconWidth = RoleIsUpdateLike ? 24 : 0;

            NoPackages_BackgroundText = data.NoPackages_BackgroundText;
            NoPackages_SourcesText = data.NoPackages_SourcesText;
            NoPackages_SubtitleText_Base = data.NoPackages_SubtitleText_Base;
            MainSubtitle_StillLoading = data.MainSubtitle_StillLoading;

            NoMatches_BackgroundText = data.NoMatches_BackgroundText;
            Loader = data.Loader;

            // Load UI
            InitializeComponent();

            // Selection of grid view mode
            int viewMode = Settings.GetDictionaryItem<string, int>(
                Settings.K.PackageListViewMode,
                PAGE_NAME
            );
            if (viewMode < 0 || viewMode >= ViewModeSelector.Items.Count)
                viewMode = 0;
            ViewModeSelector.SelectedIndex = viewMode;
            GenerateHeaderBarTitles();

            ToolTipService.SetToolTip(Selector_List, CoreTools.Translate("List"));
            ToolTipService.SetToolTip(Selector_Grid, CoreTools.Translate("Grid"));
            ToolTipService.SetToolTip(Selector_Icons, CoreTools.Translate("Icons"));

            MainTitle.Text = data.PageTitle;
            HeaderIcon.Glyph = data.Glyph;

            SelectAllCheckBox.IsChecked = data.PackagesAreCheckedByDefault;
            QuerySimilarResultsRadio.IsEnabled = !data.DisableSuggestedResultsRadio;
            QueryOptionsGroup.SelectedIndex = 1;
            QueryOptionsGroup.SelectedIndex = 2;
            QueryOptionsGroup.SelectedItem = QueryBothRadio;

            Loader.StartedLoading += Loader_StartedLoading;
            Loader.FinishedLoading += Loader_FinishedLoading;
            Loader.PackagesChanged += Loader_PackagesChanged;

            // Clear cached filtering result
            WrappedPackages.CollectionChanged += (_, _) => LastQueryResult = null;

            if (Loader.IsLoading)
            {
                Loader_StartedLoading(this, EventArgs.Empty);
            }
            else
            {
                Loader_FinishedLoading(this, EventArgs.Empty);
                FilterPackages();
            }
            Loader_PackagesChanged(this, new(false, [], []));

            LastPackageLoadTime = DateTime.Now;
            LocalPackagesNode.Content = CoreTools.Translate("Local");
            LocalPackagesNode.IsExpanded = false;

            ReloadButton.Click += async (_, _) => await LoadPackages();
            ReloadButton.Visibility = DISABLE_RELOAD ? Visibility.Collapsed : Visibility.Visible;

            // Handle the Enter Pressed event on the MegaQueryBlock
            MegaQueryBlock.KeyUp += (_, e) =>
            {
                if (e.Key != VirtualKey.Enter)
                    return;

                MegaQueryBlockGrid.Visibility = Visibility.Collapsed;
                QueryBlock.Text = MegaQueryBlock.Text.Trim();
                if (!DISABLE_FILTER_ON_QUERY_CHANGE)
                    FilterPackages(true);
            };

            // Hande the MegaQueryBlock search button click
            MegaFindButton.Click += (_, _) =>
            {
                MegaQueryBlockGrid.Visibility = Visibility.Collapsed;
                QueryBlock.Text = MegaQueryBlock.Text.Trim();
                FilterPackages(true);
            };

            // Handle when a source is clicked
            SourcesTreeView.Tapped += (_, e) =>
            {
                TreeViewNode? node =
                    (e.OriginalSource as FrameworkElement)?.DataContext as TreeViewNode;
                if (node is null)
                {
                    return;
                }

                if (SourcesTreeView.SelectedNodes.Contains(node))
                {
                    SourcesTreeView.SelectedNodes.Remove(node);
                }
                else
                {
                    SourcesTreeView.SelectedNodes.Add(node);
                }

                FilterPackages();
            };

            // Handle when a source is double-clicked
            SourcesTreeView.RightTapped += (_, e) =>
            {
                TreeViewNode? node =
                    (e.OriginalSource as FrameworkElement)?.DataContext as TreeViewNode;
                if (node is null)
                {
                    return;
                }

                SourcesTreeView.SelectedNodes.Clear();
                SourcesTreeView.SelectedNodes.Add(node);
                FilterPackages();
            };

            if (MEGA_QUERY_BOX_ENABLED)
            {
                MegaQueryBlockGrid.Visibility = Visibility.Visible;
                MegaQueryBlock.Focus(FocusState.Programmatic);
                BackgroundText.Visibility = Visibility.Collapsed;
            }

            _searchPlaceholder = CoreTools.Translate("Search for packages");
            MegaQueryBlock.PlaceholderText = _searchPlaceholder;
            InstantSearchCheckbox.IsChecked = !Settings.GetDictionaryItem<string, bool>(
                Settings.K.DisableInstantSearch,
                PAGE_NAME
            );

            HeaderIcon.FontWeight = new Windows.UI.Text.FontWeight(700);

            NameHeader.Click += (_, _) => SortPackagesBy(ObservablePackageCollection.Sorter.Name);
            IdHeader.Click += (_, _) => SortPackagesBy(ObservablePackageCollection.Sorter.Id);
            VersionHeader.Click += (_, _) =>
                SortPackagesBy(ObservablePackageCollection.Sorter.Version);
            NewVersionHeader.Click += (_, _) =>
                SortPackagesBy(ObservablePackageCollection.Sorter.NewVersion);
            SourceHeader.Click += (_, _) =>
                SortPackagesBy(ObservablePackageCollection.Sorter.Source);

            OrderByName_Menu.Click += (_, _) =>
                SortPackagesBy(ObservablePackageCollection.Sorter.Name);
            OrderById_Menu.Click += (_, _) => SortPackagesBy(ObservablePackageCollection.Sorter.Id);
            OrderByVer_Menu.Click += (_, _) =>
                SortPackagesBy(ObservablePackageCollection.Sorter.Version);
            OrderByNewVer_Menu.Click += (_, _) =>
                SortPackagesBy(ObservablePackageCollection.Sorter.NewVersion);
            OrderBySrc_Menu.Click += (_, _) =>
                SortPackagesBy(ObservablePackageCollection.Sorter.Source);

            OrderAsc_Menu.Click += (_, _) => SortPackagesBy(ascendent: true);
            OrderDesc_Menu.Click += (_, _) => SortPackagesBy(ascendent: false);

            GenerateToolBar();
            var menu = GenerateContextMenu();

            PackageList_List.ContextFlyout = menu;
            PackageList_Grid.ContextFlyout = menu;
            PackageList_Icons.ContextFlyout = menu;

            Loaded += (_, _) => ChangeFilteringPaneLayout();
            UpdateSortingMenu();
        }

        private void GenerateHeaderBarTitles()
        {
            if (ViewModeSelector.SelectedIndex == 0)
            {
                NameHeader.Content = CoreTools.Translate("Package Name");
                IdHeader.Content = CoreTools.Translate("Package ID");
                VersionHeader.Content = CoreTools.Translate("Version");
                NewVersionHeader.Content = CoreTools.Translate("New version");
                SourceHeader.Content = CoreTools.Translate("Source");
            }
            else
            {
                NameHeader.Content = "";
                IdHeader.Content = "";
                VersionHeader.Content = "";
                NewVersionHeader.Content = "";
                SourceHeader.Content = "";
            }
        }

        private void Loader_PackagesChanged(
            object? sender,
            PackagesChangedEvent packagesChangedEvent
        )
        {
            // Ensure we are in the UI thread
            if (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() is null)
            {
                DispatcherQueue.TryEnqueue(() =>
                    Loader_PackagesChanged(sender, packagesChangedEvent)
                );
                return;
            }

            // Procedural package upgrade
            if (packagesChangedEvent.ProceduralChange)
            {
                // Add added packages
                foreach (var package in packagesChangedEvent.AddedPackages)
                {
                    if (WrappedPackages.Where(w => w.Package.Equals(package)).Any())
                        continue;

                    WrappedPackages.Add(new PackageWrapper(package, this));
                    AddPackageToSourcesList(package);
                }

                // Remove removed packages
                var toRemove = new List<PackageWrapper>();
                foreach (var package in packagesChangedEvent.RemovedPackages)
                    foreach (var match in WrappedPackages.Where(w => w.Package.Equals(package)))
                    {
                        toRemove.Add(match);
                    }

                foreach (var wrapper in toRemove)
                {
                    wrapper.Dispose();
                    WrappedPackages.Remove(wrapper);
                }
            }
            else
            {
                // Reset internal package cache, and update from loader
                foreach (var wrapper in WrappedPackages)
                    wrapper.Dispose();
                WrappedPackages.Clear();
                ClearSourcesList();
                foreach (var package in Loader.Packages)
                {
                    WrappedPackages.Add(new PackageWrapper(package, this));
                    AddPackageToSourcesList(package);
                }
            }
            FilterPackages();
        }

        private void Loader_FinishedLoading(object? sender, EventArgs e)
        {
            // Ensure we are in the UI thread
            if (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() is null)
            {
                DispatcherQueue.TryEnqueue(() => Loader_FinishedLoading(sender, e));
                return;
            }

            LoadingProgressBar.Visibility = Visibility.Collapsed;
            // Required to update UI labels
            LastPackageLoadTime = DateTime.Now;
            UpdatePackageCount();
            WhenPackagesLoaded(ReloadReason.External);
        }

        private void Loader_StartedLoading(object? sender, EventArgs e)
        {
            // Ensure we are in the UI thread
            if (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() is null)
            {
                DispatcherQueue.TryEnqueue(() => Loader_StartedLoading(sender, e));
                return;
            }
            LoadingProgressBar.Visibility = Visibility.Visible;
            UpdatePackageCount();
        }

        public void SearchTriggered()
        {
            QueryBlock.Focus(FocusState.Pointer);
        }

        public void ReloadTriggered()
        {
            if (DISABLE_RELOAD)
                return;
            _ = LoadPackages(ReloadReason.Manual);
        }

        public void SelectAllTriggered()
        {
            if (QueryBlock.FocusState == FocusState.Unfocused)
            {
                if (!SelectAllCheckBox.IsChecked ?? false)
                {
                    SelectAllCheckBox.IsChecked = true;
                    FilteredPackages.SelectAll();
                }
                else
                {
                    SelectAllCheckBox.IsChecked = false;
                    FilteredPackages.ClearSelection();
                }
            }
        }

        protected void AddPackageToSourcesList(IPackage package)
        {
            IManagerSource source = package.Source;
            if (!UsedManagers.Contains(source.Manager))
            {
                UsedManagers.Add(source.Manager);
                var node = new TreeViewNode
                {
                    Content =
                        source.Manager.DisplayName
                        + "                                                                                    .",
                    IsExpanded = false,
                };
                SourcesTreeView.RootNodes.Add(node);

                // Smart way to decide whether to check a source or not.
                // - Always check a source by default if no sources are present
                // - Otherwise, Check a source only if half of the sources have already been checked
                if (SourcesTreeView.RootNodes.Count == 0)
                {
                    SourcesTreeView.SelectedNodes.Add(node);
                }
                else if (SourcesTreeView.SelectedNodes.Count >= SourcesTreeView.RootNodes.Count / 2)
                {
                    SourcesTreeView.SelectedNodes.Add(node);
                }

                RootNodeForManager.TryAdd(source.Manager, node);
                UsedSourcesForManager.TryAdd(source.Manager, []);
                SourcesPlaceholderText.Visibility = Visibility.Collapsed;
                SourcesTreeViewGrid.Visibility = Visibility.Visible;
            }

            if (
                (
                    !UsedSourcesForManager.ContainsKey(source.Manager)
                    || !UsedSourcesForManager[source.Manager].Contains(source)
                ) && source.Manager.Capabilities.SupportsCustomSources
            )
            {
                UsedSourcesForManager[source.Manager].Add(source);
                TreeViewNode item = new()
                {
                    Content =
                        source.Name
                        + "                                                                                    .",
                };
                NodesForSources.TryAdd(source, item);

                if (source.IsVirtualManager)
                {
                    LocalPackagesNode.Children.Add(item);
                    if (!SourcesTreeView.RootNodes.Contains(LocalPackagesNode))
                    {
                        SourcesTreeView.RootNodes.Add(LocalPackagesNode);
                        SourcesTreeView.SelectedNodes.Add(LocalPackagesNode);
                    }
                }
                else
                {
                    RootNodeForManager[source.Manager].Children.Add(item);
                }
            }
        }

        private void FilterOptionsChanged(object sender, RoutedEventArgs e)
        {
            if (QueryBothRadio is null)
            {
                return;
            }

            FilterPackages(true);
        }

        private void InstantSearchValueChanged(object sender, RoutedEventArgs e) =>
            Settings.SetDictionaryItem(
                Settings.K.DisableInstantSearch,
                PAGE_NAME,
                !InstantSearchCheckbox.IsChecked
            );

        private void SourcesTreeView_SelectionChanged(
            TreeView sender,
            TreeViewSelectionChangedEventArgs args
        ) => FilterPackages();

        public virtual async Task LoadPackages() => await LoadPackages(ReloadReason.External);

        protected void ClearSourcesList()
        {
            UsedManagers.Clear();
            SourcesTreeView?.RootNodes?.Clear();
            UsedSourcesForManager.Clear();
            RootNodeForManager.Clear();
            NodesForSources.Clear();
            LocalPackagesNode?.Children?.Clear();
        }

        /// <summary>
        /// Reload the packages for this Page
        /// Calling this method will trigger a reload on the associated PackageLoader, unless it is already loading packages.
        /// </summary>
        protected async Task LoadPackages(ReloadReason reason)
        {
            if (
                !Loader.IsLoading
                && (
                    !Loader.IsLoaded
                    || reason == ReloadReason.External
                    || reason == ReloadReason.Manual
                    || reason == ReloadReason.Automated
                )
            )
            {
                Loader.ClearPackages(emitFinishSignal: false);
                await Loader.ReloadPackages();
            }
        }

        private void SelectAndScrollTo(int index, bool focus)
        {
            if (index < 0 || index >= FilteredPackages.Count)
                return;

            CurrentPackageList.Select(index);

            double position;
            if (CurrentPackageList.Layout is StackLayout)
            {
                position = index * 39;
            }
            else if (CurrentPackageList.Layout is UniformGridLayout gl)
            {
                int columnCount = (int)(
                    (CurrentPackageList.ActualWidth - 8) / (gl.MinItemWidth + 8)
                );
                int row = index / Math.Max(columnCount, 1);
                position = Math.Max(row - 1, 0) * (gl.MinItemHeight + 8);
                Debug.WriteLine($"pos: {position}, colCount:{columnCount}, {row}");
            }
            else
            {
                throw new InvalidCastException("The layout was not recognized");
            }

            if (
                position < CurrentPackageList.ScrollView.VerticalOffset
                || position
                    > CurrentPackageList.ScrollView.VerticalOffset
                        + CurrentPackageList.ScrollView.ActualHeight
            )
            {
                CurrentPackageList.ScrollView.ScrollTo(
                    0,
                    position,
                    new ScrollingScrollOptions(
                        ScrollingAnimationMode.Disabled,
                        ScrollingSnapPointsMode.Ignore
                    )
                );
            }

            if (focus)
                Focus(FilteredPackages[index].Package);
        }

        private void Focus(IPackage packageToFocus, int retryCount = 0)
        {
            if (retryCount > 20)
                return;

            DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                () =>
                {
                    PackageItemContainer? containerToFocus =
                        CurrentPackageList.FindDescendant<PackageItemContainer>(c =>
                            c.Package?.Equals(packageToFocus) == true
                        );
                    if (containerToFocus == null)
                    {
                        Focus(packageToFocus, ++retryCount);
                        return;
                    }

                    if (!containerToFocus.IsSelected)
                    {
                        PackageItemContainer? selectedContainer =
                            CurrentPackageList.FindDescendant<PackageItemContainer>(c =>
                                c.IsSelected
                            );
                        if (selectedContainer?.Package?.Equals(packageToFocus) == true)
                            containerToFocus = selectedContainer;
                        else
                        {
                            Focus(packageToFocus, ++retryCount);
                            return;
                        }
                    }
                    containerToFocus.Focus(FocusState.Keyboard);
                }
            );
        }

        public void PackageList_CharacterReceived(object sender, CharacterReceivedRoutedEventArgs e)
        {
            char ch = Char.ToLower(e.Character);

            if (('a' <= ch && ch <= 'z') || ('0' <= ch && ch <= '9'))
            {
                if (Environment.TickCount - LastKeyDown > QUERY_SEPARATION_TIME)
                {
                    TypeQuery = ch.ToString();
                }
                else
                {
                    TypeQuery += ch.ToString();
                }

                int IdQueryIndex = -1;
                int NameSimilarityIndex = -1;
                int IdSimilarityIndex = -1;
                bool SelectedPackage = false;
                for (int i = 0; i < FilteredPackages.Count; i++)
                {
                    if (FilteredPackages[i].Package.Name.ToLower().StartsWith(TypeQuery))
                    {
                        SelectAndScrollTo(i, true);
                        SelectedPackage = true;
                        break;
                    }
                    // To avoid jumping back high up because an ID matched it (prevent typing "wi" focusing id:"WildfireGames.0AD" instead of name:"Windows")
                    if (
                        IdQueryIndex == -1
                        && FilteredPackages[i].Package.Id.ToLower().StartsWith(TypeQuery)
                    )
                    {
                        IdQueryIndex = i;
                    }
                    if (
                        NameSimilarityIndex == -1
                        && FilteredPackages[i].Package.Name.ToLower().Contains(TypeQuery)
                    )
                    {
                        NameSimilarityIndex = i;
                    }
                    if (
                        IdSimilarityIndex == -1
                        && FilteredPackages[i].Package.Id.ToLower().Contains(TypeQuery)
                    )
                    {
                        IdSimilarityIndex = i;
                    }
                }
                int QueryIndex =
                    IdQueryIndex > -1
                        ? IdQueryIndex
                        : (NameSimilarityIndex > -1 ? NameSimilarityIndex : IdSimilarityIndex);
                if (!SelectedPackage)
                {
                    bool SameChars = true;
                    char LastChar = TypeQuery.ToCharArray()[0];
                    foreach (var c in TypeQuery)
                    {
                        if (c != LastChar)
                        {
                            SameChars = false;
                            break;
                        }
                        LastChar = c;
                    }

                    if (SameChars)
                    {
                        int IndexOffset = TypeQuery.Length - 1;
                        int FirstIdx = -1;
                        int LastIdx = -1;
                        for (int idx = 0; idx < FilteredPackages.Count; idx++)
                        {
                            if (FilteredPackages[idx].Package.Name.ToLower().StartsWith(LastChar))
                            {
                                if (FirstIdx == -1)
                                    FirstIdx = idx;
                                LastIdx = idx;
                            }
                            else if (FirstIdx > -1)
                            {
                                // Break after the LastIdx has been set
                                break;
                            }
                        }

                        SelectAndScrollTo(
                            FirstIdx + (IndexOffset % (LastIdx - FirstIdx + 1)),
                            true
                        );
                    }
                    else if (QueryIndex > -1)
                    {
                        SelectAndScrollTo(QueryIndex, true);
                    }
                }
            }
            LastKeyDown = Environment.TickCount;
        }

        /// <summary>
        /// Applies labels, tooltips and icons to all the menubar items
        /// </summary>
        /// <param name="Labels"></param>
        /// <param name="Icons"></param>
        /// <exception cref="InvalidCastException"></exception>
        protected void ApplyTextAndIconsToToolbar(
            IDictionary<DependencyObject, string> Labels,
            IDictionary<DependencyObject, IconType> Icons
        )
        {
            foreach (DependencyObject item in Labels.Keys)
            {
                string text = Labels[item].Trim();
                ToolTipService.SetToolTip(item, text);
                if (item is AppBarButton toolButton)
                {
                    toolButton.IsCompact = Labels[toolButton][0] == ' ';
                    if (toolButton.IsCompact)
                    {
                        toolButton.LabelPosition = CommandBarLabelPosition.Collapsed;
                    }
                    toolButton.Label = text;
                }
                else if (item is BetterMenuItem menuItem)
                {
                    menuItem.UntranslatedText = text;
                }
                else
                    throw new InvalidCastException(
                        "item must be of type AppBarButton or MenuFlyoutButton"
                    );
            }

            foreach (DependencyObject item in Icons.Keys)
            {
                var icon = Icons[item];
                if (item is AppBarButton barButton)
                {
                    barButton.Icon = new LocalIcon(icon);
                }
                else if (item is BetterMenuItem menuItem)
                {
                    menuItem.IconName = icon;
                }
                else
                    throw new InvalidCastException(
                        "item must be of type AppBarButton or MenuFlyoutButton"
                    );
            }
        }

        /// <summary>
        /// Will filter the packages with the query on QueryBlock.Text and put the
        /// resulting packages on the ItemsView
        /// </summary>
        public void FilterPackages(bool forceQueryUpdate = false)
        {
            var previousSelection = CurrentPackageList.SelectedItem as PackageWrapper;

            List<IManagerSource> visibleSources = [];
            List<IPackageManager> visibleManagers = [];

            if (SourcesTreeView.SelectedNodes.Count > 0)
            {
                foreach (TreeViewNode node in SourcesTreeView.SelectedNodes)
                {
                    if (NodesForSources.Values.Contains(node))
                    {
                        visibleSources.Add(NodesForSources.First(x => x.Value == node).Key);
                    }
                    else if (RootNodeForManager.Values.Contains(node))
                    {
                        IPackageManager manager = RootNodeForManager
                            .First(x => x.Value == node)
                            .Key;
                        visibleManagers.Add(manager);
                        if (!manager.Capabilities.SupportsCustomSources)
                            continue;

                        foreach (
                            IManagerSource source in manager.SourcesHelper.Factory.GetAvailableSources()
                        )
                            if (!visibleSources.Contains(source))
                                visibleSources.Add(source);
                    }
                }
            }

            // Filter only by query when needed
            if (forceQueryUpdate || LastQueryResult is null)
            {
                // Load applied filters and prepare query
                List<Func<string, string>> appliedFilters = [];
                if (UpperLowerCaseCheckbox.IsChecked is false)
                    appliedFilters.Add(FilterHelpers.NormalizeCase);
                if (IgnoreSpecialCharsCheckbox.IsChecked is true)
                    appliedFilters.Add(FilterHelpers.NormalizeSpecialCharacters);

                string treatedQuery = QueryBlock.Text.Trim();
                foreach (var filter in appliedFilters)
                    treatedQuery = filter(treatedQuery);
                // treatedQuery now has the appropiate content

                if (QueryIdRadio.IsChecked is true)
                    LastQueryResult = WrappedPackages.Where(wrapper =>
                        FilterHelpers.NameContains(wrapper.Package, treatedQuery, appliedFilters)
                    );
                else if (QueryNameRadio.IsChecked is true)
                    LastQueryResult = WrappedPackages.Where(wrapper =>
                        FilterHelpers.IdContains(wrapper.Package, treatedQuery, appliedFilters)
                    );
                else if (QueryBothRadio.IsChecked is true)
                    LastQueryResult = WrappedPackages.Where(wrapper =>
                        FilterHelpers.NameOrIdContains(
                            wrapper.Package,
                            treatedQuery,
                            appliedFilters
                        )
                    );
                else if (QueryExactMatch.IsChecked == true)
                    LastQueryResult = WrappedPackages.Where(wrapper =>
                        FilterHelpers.NameOrIdExactMatch(
                            wrapper.Package,
                            treatedQuery,
                            appliedFilters
                        )
                    );
                else // QuerySimilarResultsRadio == true
                    LastQueryResult = WrappedPackages;
            }

            List<PackageWrapper> matchingList_selectedSources = [];

            foreach (var match in LastQueryResult)
            {
                if (
                    visibleSources.Contains(match.Package.Source)
                    || (
                        !match.Package.Manager.Capabilities.SupportsCustomSources
                        && visibleManagers.Contains(match.Package.Manager)
                    )
                )
                {
                    matchingList_selectedSources.Add(match);
                }
            }

            FilteredPackages.FromRange(matchingList_selectedSources);
            UpdatePackageCount();

            if (previousSelection is not null)
            {
                for (int i = 0; i < FilteredPackages.Count; i++)
                {
                    if (FilteredPackages[i].Package.Equals(previousSelection.Package))
                    {
                        SelectAndScrollTo(i, false);
                        break;
                    }
                }
            }
            else
            {
                _ = ForceRedrawByScroll();
            }

            if (!Settings.Get(Settings.K.DisableIconsOnPackageLists))
                _ = LoadIconsForNewPackages();
        }

        /// <summary>
        /// Updates the UI to reflect the current amount of packages
        /// </summary>
        public void UpdatePackageCount()
        {
            var selected = FilteredPackages.Where(p => p.IsChecked).Count();
            var unSelected = FilteredPackages.Where(p => !p.IsChecked).Count();
            if (selected is 0 && unSelected is not 0)
                SelectAllCheckBox.IsChecked = false;
            else if (selected is not 0 && unSelected is 0)
                SelectAllCheckBox.IsChecked = true;
            else if (selected is not 0 && unSelected is not 0)
                SelectAllCheckBox.IsChecked = null;

            string GetSubtitleText()
            {
                string r =
                    CoreTools.Translate(
                        "{0} packages were found, {1} of which match the specified filters.",
                        FilteredPackages.Count,
                        Loader.Count()
                    )
                    + " ("
                    + CoreTools.Translate("{0} selected", selected)
                    + ")";

                if (SHOW_LAST_CHECKED_TIME)
                    r +=
                        " "
                        + CoreTools.Translate(
                            "(Last checked: {0})",
                            LastPackageLoadTime.ToString(CultureInfo.CurrentCulture)
                        );
                return r;
            }

            if (FilteredPackages.Any())
            {
                BackgroundText.Text = NoPackages_BackgroundText;
                BackgroundText.Visibility = Loader.Any()
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                MainSubtitle.Text = GetSubtitleText();
            }
            else
            {
                if (LoadingProgressBar.Visibility is Visibility.Collapsed)
                {
                    if (Loader.Any())
                    {
                        BackgroundText.Text = NoMatches_BackgroundText;
                        SourcesPlaceholderText.Visibility = Visibility.Collapsed;
                        MainSubtitle.Text = GetSubtitleText();
                    }
                    else
                    {
                        BackgroundText.Text = NoPackages_BackgroundText;
                        SourcesPlaceholderText.Text = NoPackages_SourcesText;
                        SourcesPlaceholderText.Visibility = Visibility.Visible;
                        MainSubtitle.Text = NoPackages_SubtitleText;
                    }

                    BackgroundText.Visibility = Visibility.Visible;
                }
                else
                {
                    BackgroundText.Visibility = Loader.Any()
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                    BackgroundText.Text = MainSubtitle_StillLoading;
                    SourcesPlaceholderText.Visibility = Loader.Any()
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                    SourcesPlaceholderText.Text = MainSubtitle_StillLoading;
                    MainSubtitle.Text = MainSubtitle_StillLoading;
                }
            }

            if (MegaQueryBlockGrid.Visibility == Visibility.Visible)
            {
                BackgroundText.Visibility = Visibility.Collapsed;
            }
            WhenPackageCountUpdated();
        }

        /// <summary>
        /// Changes how the packages are sorted
        /// </summary>
        /// <param name="sorter">The information with which to sort the packages</param>
        public void SortPackagesBy(ObservablePackageCollection.Sorter sorter)
        {
            if (sorter == FilteredPackages.CurrentSorter)
                FilteredPackages.Descending = !FilteredPackages.Descending;
            FilteredPackages.SetSorter(sorter);
            FilteredPackages.Sort();
            UpdateSortingMenu();
        }

        public void SortPackagesBy(bool ascendent)
        {
            FilteredPackages.Descending = !ascendent;
            FilteredPackages.Sort();
            UpdateSortingMenu();
        }

        private void UpdateSortingMenu()
        {
            OrderByName_Menu.IsChecked =
                FilteredPackages.CurrentSorter is ObservablePackageCollection.Sorter.Name;
            OrderById_Menu.IsChecked =
                FilteredPackages.CurrentSorter is ObservablePackageCollection.Sorter.Id;
            OrderByVer_Menu.IsChecked =
                FilteredPackages.CurrentSorter is ObservablePackageCollection.Sorter.Version;
            OrderByNewVer_Menu.IsChecked =
                FilteredPackages.CurrentSorter is ObservablePackageCollection.Sorter.NewVersion;
            OrderBySrc_Menu.IsChecked =
                FilteredPackages.CurrentSorter is ObservablePackageCollection.Sorter.Source;

            OrderAsc_Menu.IsChecked = !FilteredPackages.Descending;
            OrderDesc_Menu.IsChecked = FilteredPackages.Descending;

            OrderByButton.Content = FilteredPackages.CurrentSorter switch
            {
                ObservablePackageCollection.Sorter.Name => CoreTools.Translate("Name"),
                ObservablePackageCollection.Sorter.Id => CoreTools.Translate("Id"),
                ObservablePackageCollection.Sorter.Version => CoreTools.Translate("Version"),
                ObservablePackageCollection.Sorter.NewVersion => CoreTools.Translate("New version"),
                ObservablePackageCollection.Sorter.Source => CoreTools.Translate("Source"),
                _ => throw new InvalidDataException(),
            };
        }

        protected void SelectAllSourcesButton_Click(object sender, RoutedEventArgs e)
        {
            SourcesTreeView.SelectAll();
        }

        protected void ClearSourceSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            SourcesTreeView.SelectedItems.Clear();
            FilterPackages();
        }

        protected void ShowDetailsForPackage(IPackage? package, TEL_InstallReferral referral)
        {
            if (package is null)
                return;

            if (package.Source.IsVirtualManager || package is InvalidImportedPackage)
            {
                DialogHelper.ShowDismissableBalloon(
                    CoreTools.Translate("Something went wrong"),
                    CoreTools.Translate(
                        "\"{0}\" is a local package and does not have available details",
                        package.Name
                    )
                );
                return;
            }

            _ = DialogHelper.ShowPackageDetails(package, PAGE_ROLE, referral);
        }

        protected void OpenPackageInstallLocation(IPackage? package)
        {
            string? path = package?.Manager.DetailsHelper.GetInstallLocation(package);
            CoreTools.Launch(path);
        }

        protected void SharePackage(IPackage? package)
        {
            if (package is null)
                return;

            DialogHelper.SharePackage(package);
        }

        protected async Task ShowInstallationOptionsForPackage(IPackage? package)
        {
            if (package is null)
                return;

            if (package.Source.IsVirtualManager || package is InvalidImportedPackage)
            {
                DialogHelper.ShowDismissableBalloon(
                    CoreTools.Translate("Something went wrong"),
                    CoreTools.Translate(
                        "\"{0}\" is a local package and is not compatible with this feature",
                        package.Name
                    )
                );
                return;
            }

            if (await DialogHelper.ShowInstallatOptions_Continue(package, PAGE_ROLE))
            {
                PerformMainPackageAction(package);
            }
        }

        private void SidepanelWidth_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            int rawWidth = (int)e.NewSize.Width;
            if (rawWidth == (int)(e.NewSize.Width / 10) || rawWidth == 25)
            {
                return;
            }

            int newWidth = Math.Clamp(rawWidth, 0, 600);

            if (newWidth < 100)
            {
                HideFilteringPane();
                Settings.SetDictionaryItem(Settings.K.SidepanelWidths, PAGE_NAME, 250);
            }
            else
            {
                FilteringPanel.OpenPaneLength = newWidth;
                ToggleFiltersButtonWidth.MinWidth = newWidth;
                Settings.SetDictionaryItem(Settings.K.SidepanelWidths, PAGE_NAME, newWidth);
            }
        }

        protected void PerformMainPackageAction(IPackage? package)
        {
            if (package is null)
            {
                return;
            }

            if (PAGE_ROLE == OperationType.Install)
            {
                _ = MainApp.Operations.Install(package, TEL_InstallReferral.DIRECT_SEARCH);
            }
            else if (PAGE_ROLE == OperationType.Update)
            {
                _ = MainApp.Operations.Update(package);
            }
            else // if (PageRole == OperationType.Uninstall)
            {
                _ = MainApp.Operations.ConfirmAndUninstall(package);
            }
        }

        public void FocusPackageList()
        {
            if (MEGA_QUERY_BOX_ENABLED)
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
                    () => MegaQueryBlock.Focus(FocusState.Programmatic));
            else
                CurrentPackageList.Focus(FocusState.Programmatic);
        }

        public async Task ShowContextMenu(PackageWrapper wrapper)
        {
            CurrentPackageList.Select(wrapper.Index);
            await Task.Delay(20);
            if (_lastContextMenuButtonTapped is not null)
                (CurrentPackageList.ContextFlyout as BetterMenu)?.ShowAt(
                    _lastContextMenuButtonTapped,
                    new FlyoutShowOptions { Placement = FlyoutPlacementMode.RightEdgeAlignedTop }
                );
            WhenShowingContextMenu(wrapper.Package);
        }

        public void PackageItemContainer_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is PackageItemContainer container && container.Package is not null)
            {
                CurrentPackageList.Select(container.Wrapper.Index);
                container.Focus(FocusState.Keyboard);
                WhenShowingContextMenu(container.Package);
            }
        }

        public void PackageItemContainer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is PackageItemContainer container && container.Package is not null)
            {
                CurrentPackageList.Select(container.Wrapper.Index);
                container.Focus(FocusState.Keyboard);

                TEL_InstallReferral referral = TEL_InstallReferral.ALREADY_INSTALLED;
                if (PAGE_NAME == "Bundles")
                    referral = TEL_InstallReferral.FROM_BUNDLE;
                if (PAGE_NAME == "Discover")
                    referral = TEL_InstallReferral.DIRECT_SEARCH;
                ShowDetailsForPackage(container.Package, referral);
            }
        }

        private void SelectAllCheckBox_ValueChanged(object sender, RoutedEventArgs e)
        {
            if (SelectAllCheckBox.IsChecked == true)
            {
                FilteredPackages.SelectAll();
            }
            else
            {
                FilteredPackages.ClearSelection();
            }
        }

        private async Task ForceRedrawByScroll()
        {
            if (CurrentPackageList is not null)
            {
                CurrentPackageList.ScrollView?.ScrollBy(0, 1);
                await Task.Delay(10);
                CurrentPackageList.ScrollView?.ScrollBy(0, -1);
            }
        }

        private void ToggleFiltersButton_Click(object sender, RoutedEventArgs e)
        {
            if (FilteringPanel.DisplayMode is SplitViewDisplayMode.Inline)
            {
                Settings.SetDictionaryItem(
                    Settings.K.HideToggleFilters,
                    PAGE_NAME,
                    !ToggleFiltersButton.IsChecked ?? false
                );
            }

            if (ToggleFiltersButton.IsChecked ?? false)
            {
                ShowFilteringPane();
            }
            else
            {
                HideFilteringPane();
            }
        }

        private void HideFilteringPane()
        {
            FilteringPanel.IsPaneOpen = false;
            PackagesListGrid.Margin = new Thickness(0, 0, 0, 0);
            ToggleFiltersButtonWidth.MinWidth = 4;
        }

        private void ShowFilteringPane()
        {
            if (FilteringPanel.DisplayMode is SplitViewDisplayMode.Inline)
            {
                int finalWidth = 250;
                try
                {
                    finalWidth = Settings.GetDictionaryItem<string, int>(
                        Settings.K.SidepanelWidths,
                        PAGE_NAME
                    );
                    if (finalWidth == 0)
                        finalWidth = 250;
                }
                catch
                {
                    Settings.SetDictionaryItem(Settings.K.SidepanelWidths, PAGE_NAME, 250);
                }
                FilteringPanel.OpenPaneLength = finalWidth;
                ToggleFiltersButtonWidth.MinWidth = finalWidth;
                PackagesListGrid.Margin = new Thickness(12, 0, 0, 0);
            }
            else
            {
                FilteringPanel.OpenPaneLength = 250;
                ToggleFiltersButtonWidth.MinWidth = 4;

                if (this.ActualTheme is ElementTheme.Dark)
                {
                    SidePanel.Background = new AcrylicBrush()
                    {
                        TintColor = Color.FromArgb(0, 20, 20, 20),
                        TintOpacity = 0.4,
                        FallbackColor = Color.FromArgb(0, 20, 20, 20),
                        TintLuminosityOpacity = 0.8,
                    };
                }
                else
                {
                    SidePanel.Background = new AcrylicBrush()
                    {
                        TintColor = Color.FromArgb(0, 250, 250, 250),
                        TintOpacity = 0.4,
                        FallbackColor = Color.FromArgb(0, 250, 250, 250),
                        TintLuminosityOpacity = 0.8,
                    };
                }
            }
            FilteringPanel.IsPaneOpen = true;
            ToggleFiltersButton.IsChecked = true;
        }

        private async Task LoadIconsForNewPackages()
        {
            var PackagesWithoutIcon = new List<PackageWrapper>();
            // Get the packages to be updated.
            foreach (var wrapper in FilteredPackages)
            {
                if (wrapper.IconWasLoaded)
                    continue;
                wrapper.IconWasLoaded = true;
                PackagesWithoutIcon.Add(wrapper);
            }

            // Load their icons, one at a time.
            foreach (var wrapper in PackagesWithoutIcon)
            {
                var icon = await Task.Run(wrapper.Package.GetIconUrlIfAny);
                if (icon is not null)
                    wrapper.PackageIcon = icon;
            }
        }

        public void OnEnter()
        {
            Visibility = Visibility.Visible;
            IsEnabled = true;
        }

        public void OnLeave()
        {
            Visibility = Visibility.Collapsed;
            IsEnabled = false;
        }

        public void PackageItemContainer_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (
                e.Key
                    is not (
                        VirtualKey.Up
                        or VirtualKey.Down
                        or VirtualKey.Home
                        or VirtualKey.End
                        or VirtualKey.Enter
                        or VirtualKey.Space
                    )
                || sender is not PackageItemContainer packageItemContainer
            )
            {
                return;
            }

            int index = FilteredPackages.IndexOf(packageItemContainer.Wrapper);
            switch (e.Key)
            {
                case VirtualKey.Up when index > 0:
                    SelectAndScrollTo(index - 1, true);
                    e.Handled = true;
                    break;
                case VirtualKey.Down when index < FilteredPackages.Count - 1:
                    SelectAndScrollTo(index + 1, true);
                    e.Handled = true;
                    break;
                case VirtualKey.Home when index > 0:
                    SelectAndScrollTo(0, true);
                    e.Handled = true;
                    break;
                case VirtualKey.End when index < FilteredPackages.Count - 1:
                    SelectAndScrollTo(FilteredPackages.Count - 1, true);
                    e.Handled = true;
                    break;
            }

            if (e.KeyStatus.WasKeyDown)
            {
                // ignore repeated KeyDown events when pressing and holding a key
                return;
            }

            IPackage? package = packageItemContainer.Package;

            bool IS_CONTROL_PRESSED = InputKeyboardSource
                .GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(CoreVirtualKeyStates.Down);
            //bool IS_SHIFT_PRESSED = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
            bool IS_ALT_PRESSED = InputKeyboardSource
                .GetKeyStateForCurrentThread(VirtualKey.LeftMenu)
                .HasFlag(CoreVirtualKeyStates.Down);
            IS_ALT_PRESSED |= InputKeyboardSource
                .GetKeyStateForCurrentThread(VirtualKey.RightMenu)
                .HasFlag(CoreVirtualKeyStates.Down);

            if (e.Key == VirtualKey.Enter && package is not null)
            {
                if (IS_ALT_PRESSED)
                {
                    _ = ShowInstallationOptionsForPackage(package);
                    e.Handled = true;
                }
                else if (IS_CONTROL_PRESSED)
                {
                    if (package is not InvalidImportedPackage)
                    {
                        PerformMainPackageAction(package);
                        e.Handled = true;
                    }
                }
                else
                {
                    TEL_InstallReferral referral = TEL_InstallReferral.ALREADY_INSTALLED;
                    if (PAGE_NAME == "Bundles")
                        referral = TEL_InstallReferral.FROM_BUNDLE;
                    if (PAGE_NAME == "Discover")
                        referral = TEL_InstallReferral.DIRECT_SEARCH;
                    ShowDetailsForPackage(package, referral);
                    e.Handled = true;
                }
            }
            else if (e.Key == VirtualKey.Space && package is not null)
            {
                package.IsChecked = !package.IsChecked;
                e.Handled = true;
            }
        }

        private void SetFilterMode_Overlay()
        {
            if (_filterPanelCurrentMode == SplitViewDisplayMode.Overlay)
                return;

            _filterPanelCurrentMode = SplitViewDisplayMode.Overlay;
            FilteringPanel.DisplayMode = SplitViewDisplayMode.Overlay;
            HideFilteringPane();
            FiltersResizer.Opacity = 0;
            ToggleFiltersButton.IsChecked = false;

            // await Task.Delay(200);
            FilteringPanel.Shadow = new ThemeShadow();
            SidePanel.BorderThickness = new Thickness(0, 1, 1, 1);

            if (FilteringPanel.Pane is ScrollViewer filters)
            {
                filters.Padding = new Thickness(8);
                filters.Margin = new Thickness(0, 1, 0, 1);
            }
        }

        private void SetFilterMode_Inline()
        {
            if (_filterPanelCurrentMode == SplitViewDisplayMode.Inline)
                return;

            _filterPanelCurrentMode = SplitViewDisplayMode.Inline;
            FilteringPanel.DisplayMode = SplitViewDisplayMode.Inline;
            SidePanel.Background = new SolidColorBrush(Colors.Transparent);
            FiltersResizer.Opacity = 1;
            SidePanel.BorderThickness = new Thickness(0);

            if (FilteringPanel.Pane is ScrollViewer filters)
            {
                filters.Padding = new Thickness(0);
                filters.Margin = new Thickness(0);
            }

            if (!Settings.GetDictionaryItem<string, bool>(Settings.K.HideToggleFilters, PAGE_NAME))
            {
                ShowFilteringPane();
            }
        }

        private void ChangeFilteringPaneLayout()
        {
            if (FilteringPanel.ActualWidth <= 0)
            {
                // Nothing, panel is not loaded yet
            }
            else if (FilteringPanel.ActualWidth < 1000)
            {
                SetFilterMode_Overlay();
            }
            else /*(FilteringPanel.ActualWidth >= 1000)*/
            {
                SetFilterMode_Inline();
            }
        }

        private void FilteringPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ChangeFilteringPaneLayout();
        }

        private void FilteringPanel_PaneClosing(
            SplitView sender,
            SplitViewPaneClosingEventArgs args
        )
        {
            ToggleFiltersButton.IsChecked = false;
            HideFilteringPane();
        }

        private void ViewModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Settings.SetDictionaryItem(
                Settings.K.PackageListViewMode,
                PAGE_NAME,
                ViewModeSelector.SelectedIndex
            );
            GenerateHeaderBarTitles();
        }

        FrameworkElement _lastContextMenuButtonTapped = null!;

        private void ContextMenuButton_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement el)
                _lastContextMenuButtonTapped = el;
        }

        private bool? _pageIsWide;
        private bool? _titleHidden;

        private void ABSTRACT_PAGE_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ActualWidth < 500)
            {
                if (_titleHidden != false)
                {
                    _titleHidden = false;
                    MainSubtitle.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                if (_titleHidden != true)
                {
                    _titleHidden = true;
                    MainSubtitle.Visibility = Visibility.Visible;
                }
            }

            if (ActualWidth < 700)
            {
                if (_pageIsWide != false)
                {
                    _pageIsWide = false;
                    MainTitle.FontSize = 18;
                }
            }
            else
            {
                if (_pageIsWide != true)
                {
                    MainTitle.FontSize = 24;
                    _pageIsWide = true;
                }
            }
        }

        public void SearchBox_TextChanged(object? sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (InstantSearchCheckbox.IsChecked is true && !DISABLE_FILTER_ON_QUERY_CHANGE)
                FilterPackages(true);

            if (MEGA_QUERY_BOX_ENABLED && QueryBlock.Text.Trim() == "")
            {
                MegaQueryBlockGrid.Visibility = Visibility.Visible;
                Loader.StopLoading();
                BackgroundText.Visibility = Visibility.Collapsed;
                ClearSourcesList();
                WrappedPackages.Clear();
                FilterPackages(true);
                MegaQueryBlock.Focus(FocusState.Programmatic);
                MegaQueryBlock.Text = "";
            }
        }

        public virtual void SearchBox_QuerySubmitted(
            object? sender,
            AutoSuggestBoxQuerySubmittedEventArgs args
        )
        {
            // Handle Enter pressed or search button pressed
            MegaQueryBlockGrid.Visibility = Visibility.Collapsed;
            if (!DISABLE_FILTER_ON_QUERY_CHANGE)
                FilterPackages(true);
        }
    }
}
