﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell.Interop;
using NuGet.Client;
using NuGet.Client.VisualStudio;
using NuGet.ProjectManagement;
using Resx = NuGet.PackageManagement.UI;

namespace NuGet.PackageManagement.UI
{
    /// <summary>
    /// Interaction logic for PackageManagerControl.xaml
    /// </summary>
    public partial class PackageManagerControl : UserControl, IVsWindowSearch
    {
        private const string NuGetRegistryKey = @"Software\NuGet";
        private const string SuppressUIDisclaimerRegistryName = "SuppressUILegalDisclaimer";

        private const int PageSize = 10;

        private bool _initialized;
        private SourceRepository _activeSource;

        // used to prevent starting new search when we update the package sources
        // list in response to PackageSourcesChanged event.
        private bool _dontStartNewSearch;

        // TODO: hook this back up
        private PackageRestoreBar _restoreBar;

        private IVsWindowSearchHost _windowSearchHost;
        private IVsWindowSearchHostFactory _windowSearchHostFactory;

        private DetailControlModel _detailModel;

        private Dispatcher _uiDispatcher;

        public PackageManagerModel Model { get; private set; }

        public PackageManagerControl(PackageManagerModel model)
            : this(model, new SimpleSearchBoxFactory())
        {
        }

        public PackageManagerControl(PackageManagerModel model, IVsWindowSearchHostFactory searchFactory)
        {
            _uiDispatcher = Dispatcher.CurrentDispatcher;
            Model = model;
            if (Model.Context.Projects.Count() == 1)
            {
                _detailModel = new PackageDetailControlModel(Model.Context.Projects);
            }
            else
            {
                _detailModel = new PackageSolutionDetailControlModel(Model.Context.Projects);
            }

            InitializeComponent();
            SetStyles();

            _windowSearchHostFactory = searchFactory;
            if (_windowSearchHostFactory != null)
            {
                _windowSearchHost = _windowSearchHostFactory.CreateWindowSearchHost(_searchControlParent);
                _windowSearchHost.SetupSearch(this);
                _windowSearchHost.IsVisible = true;
            }

            _filter.Items.Add(Resx.Resources.Filter_All);
            _filter.Items.Add(Resx.Resources.Filter_Installed);
            _filter.Items.Add(Resx.Resources.Filter_UpgradeAvailable);

            AddRestoreBar();

            _packageDetail.Control = this;
            _packageDetail.Visibility = Visibility.Hidden;

            SetTitle();

            var settings = LoadSettings();
            InitSourceRepoList(settings);
            ApplySettings(settings);

            _initialized = true;

            // register with the UI controller
            NuGetUI controller = model.UIController as NuGetUI;
            if (controller != null)
            {
                controller.PackageManagerControl = this;
            }

            Model.Context.SourceProvider.PackageSourceProvider.PackageSourcesSaved += Sources_PackageSourcesChanged;

            if (IsUILegalDisclaimerSuppressed())
            {
                _legalDisclaimer.Visibility = System.Windows.Visibility.Collapsed;
            }
        }

        private bool IsUILegalDisclaimerSuppressed()
        {
            try
            {
                var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(NuGetRegistryKey);
                var setting =
                    key == null ?
                    null :
                    key.GetValue(SuppressUIDisclaimerRegistryName) as string;
                return setting != null && setting != "0";
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void ApplySettings(UserSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            _detailModel.Options.ShowPreviewWindow = settings.ShowPreviewWindow;
            _detailModel.Options.RemoveDependencies = settings.RemoveDependencies;
            _detailModel.Options.ForceRemove = settings.ForceRemove;

            var selectedDependencyBehavior = _detailModel.Options.DependencyBehaviors
                .FirstOrDefault(d => d.Behavior == settings.DependencyBehavior);
            if (selectedDependencyBehavior != null)
            {
                _detailModel.Options.SelectedDependencyBehavior = selectedDependencyBehavior;
            }

            var selectedFileConflictAction = _detailModel.Options.FileConflictActions.
                FirstOrDefault(a => a.Action == settings.FileConflictAction);
            if (selectedFileConflictAction != null)
            {
                _detailModel.Options.SelectedFileConflictAction = selectedFileConflictAction;
            }
        }

        private void SetStyles()
        {
            if (StandaloneSwitch.IsRunningStandalone)
            {
                return;
            }

            _sourceRepoList.Style = Styles.ThemedComboStyle;
            _filter.Style = Styles.ThemedComboStyle;
        }

        private IEnumerable<SourceRepository> GetEnabledSources()
        {
            return Model.Context.SourceProvider.GetRepositories().Where(s => s.PackageSource.IsEnabled);
        }

        private void Sources_PackageSourcesChanged(object sender, EventArgs e)
        {
            // Set _dontStartNewSearch to true to prevent a new search started in
            // _sourceRepoList_SelectionChanged(). This method will start the new
            // search when needed by itself.
            _dontStartNewSearch = true;
            try
            {
                var oldActiveSource = _sourceRepoList.SelectedItem as SourceRepository;
                var newSources = GetEnabledSources();

                // Update the source repo list with the new value.
                _sourceRepoList.Items.Clear();
                foreach (var source in newSources)
                {
                    _sourceRepoList.Items.Add(source);
                }

                SetNewActiveSource(newSources, oldActiveSource);

                // force a new search explicitly if active source has changed
                if ((oldActiveSource == null && _activeSource != null) ||
                    (oldActiveSource != null && _activeSource == null) ||
                    (oldActiveSource != null && _activeSource != null &&
                    !StringComparer.OrdinalIgnoreCase.Equals(
                        oldActiveSource.PackageSource.Source,
                        _activeSource.PackageSource.Source)))
                {
                    SaveSettings();
                    SearchPackageInActivePackageSource(_windowSearchHost.SearchQuery.SearchString);
                }
            }
            finally
            {
                _dontStartNewSearch = false;
            }
        }

        private string GetSettingsKey()
        {
            string key;
            if (Model.Context.Projects.Count() == 1)
            {
                var project = Model.Context.Projects.First();
                string projectName = null;
                if (!project.TryGetMetadata<string>(NuGetProjectMetadataKeys.Name, out projectName))
                {
                    projectName = "unknown";
                }
                key = "project:" + projectName;
            }
            else
            {
                key = "solution";
            }

            return key;
        }

        // Save the settings of this doc window in the UIContext. Note that the settings
        // are not guaranteed to be persisted. We need to call Model.Context.SaveSettings()
        // to persist the settings.
        public void SaveSettings()
        {
            UserSettings settings = new UserSettings();
            if (_activeSource != null)
            {
                settings.SourceRepository = _activeSource.PackageSource.Name;
            }

            settings.ShowPreviewWindow = _detailModel.Options.ShowPreviewWindow;
            settings.RemoveDependencies = _detailModel.Options.RemoveDependencies;
            settings.ForceRemove = _detailModel.Options.ForceRemove;
            settings.DependencyBehavior = _detailModel.Options.SelectedDependencyBehavior.Behavior;
            settings.FileConflictAction = _detailModel.Options.SelectedFileConflictAction.Action;

            string key = GetSettingsKey();
            Model.Context.AddSettings(key, settings);
        }

        private UserSettings LoadSettings()
        {
            string key = GetSettingsKey();
            UserSettings settings = Model.Context.GetSettings(key);
            return settings;
        }

        /// <summary>
        /// Calculate the active source after the list of sources have been changed.
        /// </summary>
        /// <param name="newSources">The current list of sources.</param>
        /// <param name="oldActiveSource">The old active source.</param>
        private void SetNewActiveSource(IEnumerable<SourceRepository> newSources, SourceRepository oldActiveSource)
        {
            if (!newSources.Any())
            {
                _activeSource = null;
            }
            else
            {
                if (oldActiveSource == null)
                {
                    // use the first enabled source as the active source
                    _activeSource = newSources.FirstOrDefault();
                }
                else
                {
                    var s = newSources.FirstOrDefault(repo => StringComparer.CurrentCultureIgnoreCase.Equals(
                        repo.PackageSource.Name, oldActiveSource.PackageSource.Name));
                    if (s == null)
                    {
                        // the old active source does not exist any more. In this case,
                        // use the first eneabled source as the active source.
                        _activeSource = newSources.FirstOrDefault();
                    }
                    else
                    {
                        // the old active source still exists. Keep it as the active source.
                        _activeSource = s;
                    }
                }
            }

            _sourceRepoList.SelectedItem = _activeSource;
            if (_activeSource != null)
            {
                Model.Context.SourceProvider.PackageSourceProvider.SaveActivePackageSource(_activeSource.PackageSource);
            }
        }

        private void AddRestoreBar()
        {
            if (Model.Context.PackageRestoreManager != null)
            {
                _restoreBar = new PackageRestoreBar(Model.Context.PackageRestoreManager);
                _root.Children.Add(_restoreBar);
                Model.Context.PackageRestoreManager.PackagesMissingStatusChanged += packageRestoreManager_PackagesMissingStatusChanged;
            }
        }

        private void RemoveRestoreBar()
        {
            if (_restoreBar != null)
            {
                _restoreBar.CleanUp();

                // TODO: clean this up during dispose also
                Model.Context.PackageRestoreManager.PackagesMissingStatusChanged -= packageRestoreManager_PackagesMissingStatusChanged;
            }
        }

        private async void packageRestoreManager_PackagesMissingStatusChanged(object sender, PackagesMissingStatusEventArgs e)
        {
            // TODO: PackageRestoreManager fires this event even when solution is closed.
            // Don't do anything if solution is closed.
            if (!e.PackagesMissing)
            {
                await UpdateAfterPackagesMissingStatusChanged();
            }
        }

        // Refresh the UI after packages are restored.
        // Note that the PackagesMissingStatusChanged event can be fired from a non-UI thread in one case:
        // the VsSolutionManager.Init() method, which is scheduled on the thread pool. So this
        // method needs to use _uiDispatcher.
        private async Task UpdateAfterPackagesMissingStatusChanged()
        {
            if (!_uiDispatcher.CheckAccess())
            {
                await _uiDispatcher.Invoke(async () =>
                {
                    await this.UpdateAfterPackagesMissingStatusChanged();
                });

                return;
            }

            await UpdatePackageStatus();
            _packageDetail.Refresh();
        }

        private void SetTitle()
        {
            if (Model.Context.Projects.Count() > 1)
            {
                _label.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    Resx.Resources.Label_PackageManager,
                    Model.SolutionName);
            }
            else
            {
                var project = Model.Context.Projects.First();
                string projectName = null;
                if (!project.TryGetMetadata<string>(NuGetProjectMetadataKeys.Name, out projectName))
                {
                    projectName = "unknown";
                }

                _label.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    Resx.Resources.Label_PackageManager,
                    projectName);
            }
        }

        private void InitSourceRepoList(UserSettings settings)
        {
            // init source repo list
            _sourceRepoList.Items.Clear();
            var enabledSources = GetEnabledSources();
            foreach (var source in enabledSources)
            {
                _sourceRepoList.Items.Add(source);
            }

            // get active source name.
            string activeSourceName = null;

            // try saved user settings first.
            if (settings != null && !String.IsNullOrEmpty(settings.SourceRepository))
            {
                activeSourceName = settings.SourceRepository;
            }
            else
            {
                // no user settings found. Then use the active source from PackageSourceProvider.
                activeSourceName = Model.Context.SourceProvider.PackageSourceProvider.ActivePackageSourceName;
            }

            if (activeSourceName != null)
            {
                _activeSource = enabledSources
                    .FirstOrDefault(s => activeSourceName.Equals(s.PackageSource.Name, StringComparison.CurrentCultureIgnoreCase));
            }

            if (_activeSource == null)
            {
                _activeSource = enabledSources.FirstOrDefault();
            }

            if (_activeSource != null)
            {
                _sourceRepoList.SelectedItem = _activeSource;
            }
        }

        private bool ShowInstalled
        {
            get
            {
                return Resx.Resources.Filter_Installed.Equals(_filter.SelectedItem);
            }
        }

        private bool ShowUpdatesAvailable
        {
            get
            {
                return Resx.Resources.Filter_UpgradeAvailable.Equals(_filter.SelectedItem);
            }
        }

        public bool IncludePrerelease
        {
            get
            {
                return _checkboxPrerelease.IsChecked == true;
            }
        }

        internal SourceRepository ActiveSource
        {
            get
            {
                return _activeSource;
            }
        }

        private void SearchPackageInActivePackageSource(string searchText)
        {
            Filter filter = Filter.All;
            if (Resx.Resources.Filter_Installed.Equals(_filter.SelectedItem))
            {
                filter = Filter.Installed;
            }
            else if (Resx.Resources.Filter_UpgradeAvailable.Equals(_filter.SelectedItem))
            {
                filter = Filter.UpdatesAvailable;
            }

            PackageLoaderOption option = new PackageLoaderOption(filter, IncludePrerelease);
            var loader = new PackageLoader(
                option,
                Model.Context.PackageManager,
                Model.Context.Projects,
                _activeSource,
                searchText);
            _packageList.Loader = loader;
        }

        private void SettingsButtonClick(object sender, RoutedEventArgs e)
        {
            Model.UIController.LaunchNuGetOptionsDialog();
        }

        private async void PackageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await UpdateDetailPane();
        }

        /// <summary>
        /// Updates the detail pane based on the selected package
        /// </summary>
        private async Task UpdateDetailPane()
        {
            var selectedPackage = _packageList.SelectedItem as SearchResultPackageMetadata;
            if (selectedPackage == null)
            {
                _packageDetail.Visibility = Visibility.Hidden;
                _packageDetail.DataContext = null;
            }
            else
            {
                _packageDetail.Visibility = Visibility.Visible;
                _detailModel.SetCurrentPackage(selectedPackage);
                _packageDetail.DataContext = _detailModel;
                _packageDetail.ScrollToHome();

                await _detailModel.LoadPackageMetadaAsync(await _activeSource.GetResourceAsync<UIMetadataResource>(), CancellationToken.None);
            }
        }

        private static string GetPackageSourceTooltip(Configuration.PackageSource packageSource)
        {
            if (String.IsNullOrEmpty(packageSource.Description))
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} - {1}",
                    packageSource.Name,
                    packageSource.Source);
            }
            else
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} - {1} - {2}",
                    packageSource.Name,
                    packageSource.Description,
                    packageSource.Source);
            }
        }

        private void _sourceRepoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_dontStartNewSearch)
            {
                return;
            }

            _activeSource = _sourceRepoList.SelectedItem as SourceRepository;
            if (_activeSource != null)
            {
                _sourceTooltip.Visibility = Visibility.Visible;
                _sourceTooltip.DataContext = GetPackageSourceTooltip(_activeSource.PackageSource);

                Model.Context.SourceProvider.PackageSourceProvider.SaveActivePackageSource(_activeSource.PackageSource);
                SaveSettings();
                SearchPackageInActivePackageSource(_windowSearchHost.SearchQuery.SearchString);
            }
        }

        private void _filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initialized)
            {
                SearchPackageInActivePackageSource(_windowSearchHost.SearchQuery.SearchString);
            }
        }

        internal async Task UpdatePackageStatus()
        {
            if (ShowInstalled || ShowUpdatesAvailable)
            {
                // refresh the whole package list
                await _packageList.Reload();
            }
            else
            {
                // in this case, we only need to update PackageStatus of
                // existing items in the package list
                foreach (var item in _packageList.Items)
                {
                    var package = item as SearchResultPackageMetadata;
                    if (package == null)
                    {
                        continue;
                    }

                    package.Status = PackageManagerControl.GetPackageStatus(
                        package.Id,
                        Model.Context.Projects,
                        package.Versions);
                }
            }
        }

        /// <summary>
        /// Gets the status of the package specified by <paramref name="packageId"/> in
        /// the specified installation target.
        /// </summary>
        /// <param name="packageId">package id.</param>
        /// <param name="target">The installation target.</param>
        /// <param name="allVersions">List of all versions of the package.</param>
        /// <returns>The status of the package in the installation target.</returns>
        public static PackageStatus GetPackageStatus(
            string packageId,
            IEnumerable<NuGetProject> projects,
            IEnumerable<VersionInfo> allVersions)
        {
            var latestStableVersion = allVersions
                .Where(p => !p.Version.IsPrerelease)
                .Max(p => p.Version);

            List<NuGet.Packaging.PackageReference> installedPackages = new List<Packaging.PackageReference>();
            foreach (var project in projects)
            {
                var task = project.GetInstalledPackagesAsync(CancellationToken.None);
                task.Wait();
                installedPackages.AddRange(task.Result);
            }

            // Get the minimum version installed in any target project/solution
            var minimumInstalledPackage = installedPackages
                .Where(p => p != null)
                .Where(p => StringComparer.OrdinalIgnoreCase.Equals(p.PackageIdentity.Id, packageId))
                .OrderBy(r => r.PackageIdentity.Version)
                .FirstOrDefault();

            PackageStatus status;
            if (minimumInstalledPackage != null)
            {
                if (minimumInstalledPackage.PackageIdentity.Version < latestStableVersion)
                {
                    status = PackageStatus.UpdateAvailable;
                }
                else
                {
                    status = PackageStatus.Installed;
                }
            }
            else
            {
                status = PackageStatus.NotInstalled;
            }

            return status;
        }

        private void _searchControl_SearchStart(object sender, EventArgs e)
        {
            if (!_initialized)
            {
                return;
            }

            SearchPackageInActivePackageSource(_windowSearchHost.SearchQuery.SearchString);
        }

        private void _checkboxPrerelease_CheckChanged(object sender, RoutedEventArgs e)
        {
            if (!_initialized)
            {
                return;
            }

            SearchPackageInActivePackageSource(_windowSearchHost.SearchQuery.SearchString);
        }

        internal class SearchQuery : IVsSearchQuery
        {
            public uint GetTokens(uint dwMaxTokens, IVsSearchToken[] rgpSearchTokens)
            {
                return 0;
            }

            public uint ParseError
            {
                get { return 0; }
            }

            public string SearchString
            {
                get;
                set;
            }
        }

        public Guid Category
        {
            get
            {
                return Guid.Empty;
            }
        }

        public void ClearSearch()
        {
            SearchPackageInActivePackageSource(_windowSearchHost.SearchQuery.SearchString);
        }

        public IVsSearchTask CreateSearch(uint dwCookie, IVsSearchQuery pSearchQuery, IVsSearchCallback pSearchCallback)
        {
            SearchPackageInActivePackageSource(pSearchQuery.SearchString);
            return null;
        }

        public bool OnNavigationKeyDown(uint dwNavigationKey, uint dwModifiers)
        {
            // We are not interesting in intercepting navigation keys, so return "not handled"
            return false;
        }

        public void ProvideSearchSettings(IVsUIDataSource pSearchSettings)
        {
            // pSearchSettings is of type SearchSettingsDataSource. We use dynamic here
            // so that the code can be run on both dev12 & dev14. If we use the type directly,
            // there will be type mismatch error.
            dynamic settings = pSearchSettings;
            settings.ControlMinWidth = (uint)_searchControlParent.MinWidth;
            settings.ControlMaxWidth = uint.MaxValue;
            settings.SearchWatermark = GetSearchText();
        }

        // Returns the text to be displayed in the search box.
        private string GetSearchText()
        {
            var focusOnSearchKeyGesture = (KeyGesture)InputBindings.OfType<KeyBinding>().First(
                x => x.Command == Commands.FocusOnSearchBox).Gesture;
            return string.Format(CultureInfo.CurrentCulture,
                Resx.Resources.Text_SearchBoxText,
                focusOnSearchKeyGesture.GetDisplayStringForCulture(CultureInfo.CurrentCulture));
        }

        public bool SearchEnabled
        {
            get { return true; }
        }

        public IVsEnumWindowSearchFilters SearchFiltersEnum
        {
            get { return null; }
        }

        public IVsEnumWindowSearchOptions SearchOptionsEnum
        {
            get { return null; }
        }

        private void FocusOnSearchBox_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            _windowSearchHost.Activate();
        }

        public void Search(string searchText)
        {
            if (String.IsNullOrWhiteSpace(searchText))
            {
                return;
            }

            _windowSearchHost.Activate();
            _windowSearchHost.SearchAsync(new SearchQuery() { SearchString = searchText });
        }

        public void CleanUp()
        {
            _windowSearchHost.TerminateSearch();
            RemoveRestoreBar();
        }

        private void SuppressDisclaimerChecked(object sender, RoutedEventArgs e)
        {
            _legalDisclaimer.Visibility = System.Windows.Visibility.Collapsed;

            try
            {
                var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(NuGetRegistryKey);
                key.SetValue(SuppressUIDisclaimerRegistryName, "1", Microsoft.Win32.RegistryValueKind.String);
            }
            catch (Exception)
            {
            }
        }
    }
}