using KaneCode.Models;
using KaneCode.Services;
using NuGet.Packaging.Core;
using NuGet.Versioning;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KaneCode;

/// <summary>
/// NuGet Package Manager window for browsing, installing, updating, and uninstalling packages.
/// </summary>
public partial class NuGetPackageManagerWindow : Window
{
    private readonly NuGetService _nuGetService = new();
    private readonly ObservableCollection<NuGetPackageItem> _packageItems = [];
    private readonly List<string> _projectPaths = [];
    private string? _selectedProjectPath;
    private string? _selectedPackageId;
    private IReadOnlyList<NuGetVersion>? _availableVersions;
    private bool _isConnected;
    private readonly string? _highlightPackageId;

    /// <summary>
    /// Creates a new NuGet Package Manager window.
    /// </summary>
    /// <param name="projectPaths">List of .csproj file paths to manage packages for.</param>
    /// <param name="owner">Owner window.</param>
    /// <summary>
    /// Creates a new NuGet Package Manager window.
    /// </summary>
    /// <param name="projectPaths">List of .csproj file paths to manage packages for.</param>
    /// <param name="owner">Owner window.</param>
    /// <param name="highlightPackageId">Optional package ID to search for and highlight when the window opens.</param>
    public NuGetPackageManagerWindow(IReadOnlyList<string> projectPaths, Window owner, string? highlightPackageId = null)
    {
        InitializeComponent();
        Owner = owner;

        _projectPaths = projectPaths.ToList();
        _highlightPackageId = highlightPackageId;

        // Populate project selector
        foreach (var path in _projectPaths)
        {
            var displayName = Path.GetFileName(path);
            ProjectComboBox.Items.Add(new ProjectListItem(displayName, path));
        }

        if (ProjectComboBox.Items.Count > 0)
        {
            ProjectComboBox.SelectedIndex = 0;
        }

        PackageListBox.ItemsSource = _packageItems;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SelectProject();
        await InitializeConnectionAsync();
        await RefreshInstalledPackagesForCurrentProjectAsync();

        if (_projectPaths.Count == 0)
        {
            UpdateStatus("No projects loaded. Open a project or solution first.");
            SetActionButtonsEnabled(false);
        }
        else
        {
            RefreshPackageCount();
        }

        // If a highlight package was requested, search and select it
        if (!string.IsNullOrWhiteSpace(_highlightPackageId))
        {
            await HighlightPackageAsync(_highlightPackageId);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _nuGetService.Dispose();
    }

    /// <summary>
    /// Switches to the Installed tab and selects the specified package in the list.
    /// Loads installed packages asynchronously, then highlights the matching result.
    /// </summary>
    internal async Task HighlightPackageAsync(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return;
        }

        // Switch to Installed tab (index 1)
        if (MainTabControl.SelectedIndex != 1)
        {
            MainTabControl.SelectedIndex = 1;
        }

        // Load installed packages and wait for the list to populate
        await ShowInstalledPackagesAsync();

        // Find the matching package (by exact ID, case-insensitive)
        var match = _packageItems.FirstOrDefault(p =>
            string.Equals(p.Id, packageId, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            // Select and scroll to the matching item
            PackageListBox.SelectedItem = match;
            PackageListBox.ScrollIntoView(match);
        }
        else if (_packageItems.Count > 0)
        {
            // If no exact match, select the first installed package
            PackageListBox.SelectedIndex = 0;
        }

        UpdateStatus($"Showing installed package '{packageId}'.");
    }

    private void SelectProject()
    {
        _selectedProjectPath = ProjectComboBox.SelectedItem is ProjectListItem item ? item.Path : null;
    }

    private void ProjectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectProject();
        _ = RefreshInstalledPackagesForCurrentProjectAsync();
        RefreshPackageCount();

        if (!string.IsNullOrWhiteSpace(_selectedPackageId))
        {
            UpdateDetailPanelInstallState(_selectedPackageId);
        }
    }

    // ── Connection ──────────────────────────────────────────────────────

    /// <summary>
    /// Tests connectivity to NuGet.org and updates the status indicator.
    /// </summary>
    private async Task InitializeConnectionAsync()
    {
        UpdateConnectionStatus("Connecting...", isLoading: true);

        try
        {
            // Attempt a lightweight search to verify connectivity
            var testResults = await _nuGetService.SearchPackagesAsync(
                "Newtonsoft.Json",
                includePrerelease: false,
                take: 1,
                cancellationToken: CancellationToken.None).ConfigureAwait(true);

            _isConnected = true;
            UpdateConnectionStatus("Connected", isError: false);
            UpdateStatus("Ready. Connected to NuGet.org.");
        }
        catch (HttpRequestException ex)
        {
            _isConnected = false;
            UpdateConnectionStatus($"Connection failed: {ex.Message}", isError: true);
            UpdateStatus($"Cannot reach NuGet.org: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            _isConnected = false;
            UpdateConnectionStatus("Connection timed out", isError: true);
            UpdateStatus("Connection to NuGet.org timed out.");
        }
        catch (Exception ex)
        {
            _isConnected = false;
            UpdateConnectionStatus($"Error: {ex.Message}", isError: true);
            UpdateStatus($"Failed to connect: {ex.Message}");
        }
        finally
        {
            LoadingProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateConnectionStatus(string message, bool isLoading = false, bool isError = false)
    {
        ConnectionStatusIndicator.Visibility = Visibility.Visible;
        ConnectionStatusText.Text = message;
        ConnectionStatusIndicator.Background = isError
            ? (System.Windows.Media.Brush?)FindResource("DiagnosticErrorForeground")
               ?? System.Windows.Media.Brushes.Red
            : isLoading
                ? (System.Windows.Media.Brush?)FindResource("DiagnosticInfoForeground")
                   ?? System.Windows.Media.Brushes.Gray
                : (System.Windows.Media.Brush?)FindResource("DiagnosticInfoForeground")
                   ?? System.Windows.Media.Brushes.Gray;
    }

    // ── Tab switching ───────────────────────────────────────────────────

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Guard: SelectionChanged fires during XAML initialization before child elements exist.
        if (!IsLoaded)
        {
            return;
        }

        if (MainTabControl.SelectedIndex == 0) // Browse
        {
            // Clear stale results from Installed/Updates tabs
            // so the user starts fresh. They can search again.
            _packageItems.Clear();
            ClearDetailPanel();
            UpdateStatus("Enter a search term to browse NuGet.org.");
        }
        else if (MainTabControl.SelectedIndex == 1) // Installed
        {
            _ = ShowInstalledPackagesAsync();
        }
        else if (MainTabControl.SelectedIndex == 2) // Updates
        {
            _ = ShowUpdatesAvailableAsync();
        }
    }

    // ── Search ──────────────────────────────────────────────────────────

    private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = PerformSearchAsync();
            e.Handled = true;
        }
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        _ = PerformSearchAsync();
    }

    private void SearchOptions_Changed(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(SearchTextBox.Text))
        {
            _ = PerformSearchAsync();
        }
    }

    internal async Task PerformSearchAsync()
    {
        var query = SearchTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            UpdateStatus("Enter a search term.");
            return;
        }

        if (!_isConnected)
        {
            UpdateStatus("No connection to NuGet.org. Check your internet connection.");
            return;
        }

        SetSearchLoading(true);
        _packageItems.Clear();
        ClearDetailPanel();
        UpdateStatus($"Searching NuGet.org for '{query}'...");

        try
        {
            bool includePrerelease = IncludePrereleaseCheckBox.IsChecked == true;
            var results = await _nuGetService.SearchPackagesAsync(
                query,
                includePrerelease,
                cancellationToken: CancellationToken.None).ConfigureAwait(true);

            _packageItems.Clear();
            await MarkInstalledStatusAsync(results);

            foreach (var item in results)
            {
                _packageItems.Add(item);
            }

            if (results.Count == 0)
            {
                UpdateStatus($"No packages found for '{query}'.");
            }
            else
            {
                UpdateStatus($"Found {results.Count} package(s) for '{query}'.");
            }
        }
        catch (HttpRequestException ex)
        {
            UpdateStatus($"Network error: {ex.Message}");
            _isConnected = false;
            UpdateConnectionStatus("Connection lost", isError: true);
        }
        catch (TaskCanceledException)
        {
            UpdateStatus("Search timed out. Please try again.");
        }
        catch (Exception ex)
        {
            UpdateStatus($"Search failed: {ex.Message}");
        }
        finally
        {
            SetSearchLoading(false);
        }
    }

    // ── Installed / Updates tabs ────────────────────────────────────────

    internal async Task ShowInstalledPackagesAsync()
    {
        if (string.IsNullOrWhiteSpace(_selectedProjectPath))
        {
            UpdateStatus("Select a project first.");
            return;
        }

        SetSearchLoading(true);
        _packageItems.Clear();
        ClearDetailPanel();
        UpdateStatus("Loading installed packages...");

        try
        {
            var installed = NuGetService.GetInstalledPackages(_selectedProjectPath);
            _packageItems.Clear();

            foreach (var identity in installed)
            {
                var item = new NuGetPackageItem(identity)
                {
                    Title = identity.Id,
                    IsInstalled = true,
                    InstalledVersion = identity.Version
                };

                // Try to get more details from NuGet.org (only if connected)
                if (_isConnected)
                {
                    try
                    {
                        var detail = await _nuGetService.GetPackageDetailAsync(
                            identity.Id, identity.Version, CancellationToken.None).ConfigureAwait(true);

                        if (detail is not null)
                        {
                            item.Title = detail.Title;
                            item.Description = detail.Description;
                            item.Authors = detail.Authors;
                            item.ProjectUrl = detail.ProjectUrl;
                            item.LicenseUrl = detail.LicenseUrl;
                            item.IconUrl = detail.IconUrl;
                            item.Tags = detail.Tags;
                            item.TotalDownloads = detail.TotalDownloads;
                        }
                    }
                    catch
                    {
                        // Keep basic info if fetching details fails
                    }
                }

                _packageItems.Add(item);
            }

            UpdateStatus($"Installed packages: {installed.Count}");
            RefreshPackageCount();
        }
        catch (Exception ex)
        {
            UpdateStatus($"Failed to load installed packages: {ex.Message}");
        }
        finally
        {
            SetSearchLoading(false);
        }
    }

    private async Task ShowUpdatesAvailableAsync()
    {
        if (string.IsNullOrWhiteSpace(_selectedProjectPath))
        {
            UpdateStatus("Select a project first.");
            return;
        }

        if (!_isConnected)
        {
            UpdateStatus("No connection to NuGet.org. Cannot check for updates.");
            return;
        }

        SetSearchLoading(true);
        _packageItems.Clear();
        ClearDetailPanel();
        UpdateStatus("Checking for updates...");

        try
        {
            var installed = NuGetService.GetInstalledPackages(_selectedProjectPath);
            _packageItems.Clear();

            foreach (var identity in installed)
            {
                IReadOnlyList<NuGetVersion> versions;
                try
                {
                    versions = await _nuGetService.GetPackageVersionsAsync(
                        identity.Id,
                        includePrerelease: false,
                        CancellationToken.None).ConfigureAwait(true);
                }
                catch
                {
                    continue;
                }

                var latest = versions.FirstOrDefault();
                if (latest is null || latest <= identity.Version)
                {
                    continue;
                }

                var item = new NuGetPackageItem(new PackageIdentity(identity.Id, latest))
                {
                    Title = identity.Id,
                    IsInstalled = true,
                    InstalledVersion = identity.Version,
                    UpdateAvailable = true
                };

                // Try to get details
                if (_isConnected)
                {
                    try
                    {
                        var detail = await _nuGetService.GetPackageDetailAsync(
                            identity.Id, latest, CancellationToken.None).ConfigureAwait(true);
                        if (detail is not null)
                        {
                            item.Title = detail.Title;
                            item.Description = detail.Description;
                        }
                    }
                    catch { }
                }

                _packageItems.Add(item);
            }

            UpdateStatus(_packageItems.Count > 0
                ? $"{_packageItems.Count} update(s) available."
                : "All packages are up to date.");
            RefreshPackageCount();
        }
        catch (Exception ex)
        {
            UpdateStatus($"Failed to check updates: {ex.Message}");
        }
        finally
        {
            SetSearchLoading(false);
        }
    }

    private async Task RefreshInstalledPackagesForCurrentProjectAsync()
    {
        if (string.IsNullOrWhiteSpace(_selectedProjectPath))
        {
            return;
        }

        if (_packageItems.Count > 0 && MainTabControl.SelectedIndex == 0)
        {
            await MarkInstalledStatusAsync(_packageItems.ToList());
        }
    }

    // ── Detail panel ────────────────────────────────────────────────────

    private void PackageListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PackageListBox.SelectedItem is NuGetPackageItem item)
        {
            ShowPackageDetail(item);
        }
        else
        {
            ClearDetailPanel();
        }
    }

    private async void ShowPackageDetail(NuGetPackageItem item)
    {
        _selectedPackageId = item.Id;

        DetailPackageId.Text = item.Title;
        DetailVersion.Text = $"v{item.VersionText}";
        DetailAuthors.Text = string.IsNullOrWhiteSpace(item.Authors) ? "" : $"By: {item.Authors}";
        DetailDownloads.Text = item.TotalDownloads > 0 ? $"{item.TotalDownloads:N0} downloads" : "";
        DetailDescription.Text = string.IsNullOrWhiteSpace(item.Description) ? "(No description)" : item.Description;
        DetailTags.Text = string.IsNullOrWhiteSpace(item.Tags) ? "" : $"Tags: {item.Tags}";

        // Project URL
        if (item.ProjectUrl is not null)
        {
            DetailProjectUrl.Text = "Project Website";
            DetailProjectUrl.Tag = item.ProjectUrl;
            DetailProjectUrl.Visibility = Visibility.Visible;
        }
        else
        {
            DetailProjectUrl.Visibility = Visibility.Collapsed;
        }

        // Load versions from NuGet.org
        if (_isConnected)
        {
            try
            {
                bool includePrerelease = IncludePrereleaseCheckBox.IsChecked == true;
                _availableVersions = await _nuGetService.GetPackageVersionsAsync(
                    item.Id, includePrerelease, CancellationToken.None).ConfigureAwait(true);

                VersionComboBox.Items.Clear();
                // Show latest 10 versions
                var displayVersions = _availableVersions.Take(10).ToList();
                foreach (var version in displayVersions)
                {
                    VersionComboBox.Items.Add(version.ToFullString());
                }

                if (displayVersions.Count > 0)
                {
                    VersionComboBox.SelectedIndex = 0;
                }
                else
                {
                    VersionComboBox.Items.Add(item.VersionText);
                    VersionComboBox.SelectedIndex = 0;
                }
            }
            catch
            {
                VersionComboBox.Items.Clear();
                VersionComboBox.Items.Add(item.VersionText);
                VersionComboBox.SelectedIndex = 0;
            }
        }
        else
        {
            // Offline: just show the item's own version
            VersionComboBox.Items.Clear();
            VersionComboBox.Items.Add(item.VersionText);
            VersionComboBox.SelectedIndex = 0;
            _availableVersions = null;
        }

        UpdateDetailPanelInstallState(item.Id);
        DetailStatus.Text = string.Empty;
    }

    private void UpdateDetailPanelInstallState(string packageId)
    {
        if (string.IsNullOrWhiteSpace(_selectedProjectPath))
        {
            SetActionButtonsEnabled(false);
            return;
        }

        var installed = NuGetService.GetInstalledPackages(_selectedProjectPath);
        var existing = installed.FirstOrDefault(p =>
            string.Equals(p.Id, packageId, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            // Package is installed
            InstallButton.IsEnabled = false;
            InstallButton.Content = "Installed ✓";
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = "Update";
            UninstallButton.IsEnabled = true;

            // Check if a newer version is available
            if (_availableVersions is not null && _availableVersions.Count > 0)
            {
                var latest = _availableVersions.First();
                bool hasUpdate = latest > existing.Version;
                UpdateButton.IsEnabled = hasUpdate && _isConnected;
                UpdateButton.Content = hasUpdate
                    ? $"Update to {latest.ToFullString()}"
                    : "Up to date";
            }
            else
            {
                UpdateButton.Content = "Up to date";
            }
        }
        else
        {
            // Not installed
            InstallButton.IsEnabled = _isConnected;
            InstallButton.Content = "Install";
            UpdateButton.IsEnabled = false;
            UpdateButton.Content = "Update";
            UninstallButton.IsEnabled = false;
        }
    }

    private void ClearDetailPanel()
    {
        // Guard: may be called during initialization before XAML elements are resolved.
        if (DetailPackageId is null) return;

        DetailPackageId.Text = string.Empty;
        DetailVersion.Text = string.Empty;
        DetailAuthors.Text = string.Empty;
        DetailDownloads.Text = string.Empty;
        DetailDescription.Text = string.Empty;
        DetailTags.Text = string.Empty;
        DetailProjectUrl.Visibility = Visibility.Collapsed;
        DetailStatus.Text = string.Empty;
        VersionComboBox.Items.Clear();
        _selectedPackageId = null;
        _availableVersions = null;

        SetActionButtonsEnabled(false);
        InstallButton.Content = "Install";
        UpdateButton.Content = "Update";
    }

    private void SetActionButtonsEnabled(bool enabled)
    {
        InstallButton.IsEnabled = enabled;
        UpdateButton.IsEnabled = enabled;
        UninstallButton.IsEnabled = enabled;
    }

    private void VersionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_selectedPackageId))
        {
            UpdateDetailPanelInstallState(_selectedPackageId);
        }
    }

    // ── Actions: Install / Update / Uninstall ───────────────────────────

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedProjectPath) || string.IsNullOrWhiteSpace(_selectedPackageId))
        {
            return;
        }

        var versionText = VersionComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(versionText) || !NuGetVersion.TryParse(versionText, out var version))
        {
            DetailStatus.Text = "Please select a valid version.";
            return;
        }

        try
        {
            DetailStatus.Text = $"Installing {_selectedPackageId} {versionText}...";
            InstallButton.IsEnabled = false;

            var success = NuGetService.InstallPackage(_selectedProjectPath, _selectedPackageId, version);

            if (success)
            {
                DetailStatus.Text = $"✓ {_selectedPackageId} {versionText} installed successfully.";
                UpdateDetailPanelInstallState(_selectedPackageId);
                RefreshPackageCount();

                if (MainTabControl.SelectedIndex == 1)
                {
                    await ShowInstalledPackagesAsync();
                }
            }
            else
            {
                DetailStatus.Text = $"Package {_selectedPackageId} is already installed.";
                UpdateDetailPanelInstallState(_selectedPackageId);
            }

            UpdateStatus($"Installed {_selectedPackageId} {versionText}.");
        }
        catch (Exception ex)
        {
            DetailStatus.Text = $"Install failed: {ex.Message}";
            InstallButton.IsEnabled = true;
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedProjectPath) || string.IsNullOrWhiteSpace(_selectedPackageId))
        {
            return;
        }

        var versionText = VersionComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(versionText) || !NuGetVersion.TryParse(versionText, out var version))
        {
            DetailStatus.Text = "Please select a valid version.";
            return;
        }

        try
        {
            DetailStatus.Text = $"Updating {_selectedPackageId} to {versionText}...";
            UpdateButton.IsEnabled = false;

            var success = NuGetService.UpdatePackage(_selectedProjectPath, _selectedPackageId, version);

            if (success)
            {
                DetailStatus.Text = $"✓ {_selectedPackageId} updated to {versionText}.";
                UpdateDetailPanelInstallState(_selectedPackageId);
                RefreshPackageCount();

                if (MainTabControl.SelectedIndex == 2)
                {
                    await ShowUpdatesAvailableAsync();
                }
            }
            else
            {
                DetailStatus.Text = $"Package {_selectedPackageId} not found.";
            }

            UpdateStatus($"Updated {_selectedPackageId} to {versionText}.");
        }
        catch (Exception ex)
        {
            DetailStatus.Text = $"Update failed: {ex.Message}";
            UpdateButton.IsEnabled = true;
        }
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedProjectPath) || string.IsNullOrWhiteSpace(_selectedPackageId))
        {
            return;
        }

        var result = MessageBox.Show(
            $"Uninstall {_selectedPackageId} from '{Path.GetFileName(_selectedProjectPath)}'?",
            "Confirm Uninstall",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            DetailStatus.Text = $"Uninstalling {_selectedPackageId}...";
            UninstallButton.IsEnabled = false;

            var success = NuGetService.UninstallPackage(_selectedProjectPath, _selectedPackageId);

            if (success)
            {
                DetailStatus.Text = $"✓ {_selectedPackageId} uninstalled.";
                UpdateDetailPanelInstallState(_selectedPackageId);
                RefreshPackageCount();

                if (MainTabControl.SelectedIndex == 1)
                {
                    await ShowInstalledPackagesAsync();
                }
                else if (MainTabControl.SelectedIndex == 2)
                {
                    await ShowUpdatesAvailableAsync();
                }
                else
                {
                    await MarkInstalledStatusAsync(_packageItems.ToList());
                }
            }
            else
            {
                DetailStatus.Text = $"Package {_selectedPackageId} not found in project.";
            }

            UpdateStatus($"Uninstalled {_selectedPackageId}.");
        }
        catch (Exception ex)
        {
            DetailStatus.Text = $"Uninstall failed: {ex.Message}";
            UninstallButton.IsEnabled = true;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private void SetSearchLoading(bool isLoading)
    {
        LoadingProgressBar.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        SearchButton.IsEnabled = !isLoading;
    }

    private void UpdateStatus(string message)
    {
        StatusBarText.Text = message;
    }

    private void RefreshPackageCount()
    {
        if (string.IsNullOrWhiteSpace(_selectedProjectPath))
        {
            PackageCountText.Text = string.Empty;
            return;
        }

        var installed = NuGetService.GetInstalledPackages(_selectedProjectPath);
        PackageCountText.Text = $"{installed.Count} package(s)";
    }

    private async Task MarkInstalledStatusAsync(IReadOnlyList<NuGetPackageItem> items)
    {
        if (string.IsNullOrWhiteSpace(_selectedProjectPath))
        {
            return;
        }

        var installed = NuGetService.GetInstalledPackages(_selectedProjectPath);

        foreach (var item in items)
        {
            var existing = installed.FirstOrDefault(p =>
                string.Equals(p.Id, item.Id, StringComparison.OrdinalIgnoreCase));

            item.IsInstalled = existing is not null;
            item.InstalledVersion = existing?.Version;

            if (existing is not null && existing.Version < item.Version)
            {
                item.UpdateAvailable = true;
            }
        }
    }

    private void DetailProjectUrl_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is Uri uri)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = uri.ToString(),
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    /// <summary>
    /// Simple wrapper for project path display in the combo box.
    /// </summary>
    private sealed record ProjectListItem(string DisplayName, string Path);
}
