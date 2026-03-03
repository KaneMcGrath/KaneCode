using KaneCode.Theming;
using KaneCode.ViewModels;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KaneCode;

/// <summary>
/// Options dialog with categorized settings pages (General, Appearance, Hotkeys, AI Providers).
/// </summary>
public partial class OptionsWindow : Window
{
    internal const string AiSettingsCategoryName = "AI Settings";

    private bool _isInitializing = true;
    private readonly HotkeySettingsViewModel _hotkeyViewModel = new();
    private readonly AiSettingsViewModel _aiSettingsViewModel = new();
    private readonly string? _initialCategory;

    public OptionsWindow(string? initialCategory = null)
    {
        _initialCategory = initialCategory;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Set the theme combo to match the current theme
        ThemeComboBox.SelectedIndex = ThemeManager.CurrentTheme switch
        {
            AppTheme.Light => 1,
            _ => 0
        };

        // Bind the hotkeys page to its view model
        HotkeysPageBorder.DataContext = _hotkeyViewModel;

        // Bind the AI providers page to its view model
        AiProvidersPageBorder.DataContext = _aiSettingsViewModel;
        _aiSettingsViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AiSettingsViewModel.SelectedEntry))
            {
                AiDetailPanel.IsEnabled = _aiSettingsViewModel.SelectedEntry is not null;
                SyncApiKeyBox();
            }
        };

        _isInitializing = false;

        if (!string.IsNullOrWhiteSpace(_initialCategory))
        {
            SelectCategory(_initialCategory);
        }
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Guard against calls during InitializeComponent before elements are initialized
        if (_isInitializing || CategoryList.SelectedItem is not ListBoxItem item)
        {
            return;
        }

        var category = item.Content?.ToString();

        // Hide all pages
        GeneralPageBorder.Visibility = Visibility.Collapsed;
        AppearancePageBorder.Visibility = Visibility.Collapsed;
        HotkeysPageBorder.Visibility = Visibility.Collapsed;
        AiProvidersPageBorder.Visibility = Visibility.Collapsed;

        // Show selected page
        switch (category)
        {
            case "General":
                GeneralPageBorder.Visibility = Visibility.Visible;
                break;
            case "Appearance":
                AppearancePageBorder.Visibility = Visibility.Visible;
                break;
            case "Hotkeys":
                HotkeysPageBorder.Visibility = Visibility.Visible;
                break;
            case AiSettingsCategoryName:
                AiProvidersPageBorder.Visibility = Visibility.Visible;
                break;
        }
    }

    private void SelectCategory(string categoryName)
    {
        var item = CategoryList.Items
            .OfType<ListBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Content?.ToString(), categoryName, StringComparison.Ordinal));

        if (item is not null)
        {
            CategoryList.SelectedItem = item;
        }
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        var theme = ThemeComboBox.SelectedIndex switch
        {
            1 => AppTheme.Light,
            _ => AppTheme.Dark
        };

        ThemeManager.ApplyTheme(theme);
    }

    private void AssignKey_Click(object sender, RoutedEventArgs e)
    {
        _hotkeyViewModel.StartRecording();
        UpdateRecordingIndicator();
        HotkeyListView.Focus();
    }

    private void HotkeyList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _hotkeyViewModel.StartRecording();
        UpdateRecordingIndicator();
    }

    private void HotkeyList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_hotkeyViewModel.IsRecording)
        {
            return;
        }

        // Prevent default list view key handling while recording
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        if (_hotkeyViewModel.ApplyRecordedGesture(key, modifiers))
        {
            UpdateRecordingIndicator();
        }
    }

    private void UpdateRecordingIndicator()
    {
        RecordingIndicator.Visibility = _hotkeyViewModel.IsRecording
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_aiSettingsViewModel.SelectedEntry is { } entry)
        {
            entry.ApiKey = ApiKeyBox.Password;
        }
    }

    /// <summary>
    /// Pushes the stored API key into the PasswordBox when selection changes.
    /// PasswordBox.Password is not a dependency property, so this is done in code-behind.
    /// </summary>
    private void SyncApiKeyBox()
    {
        ApiKeyBox.Password = _aiSettingsViewModel.SelectedEntry?.ApiKey ?? string.Empty;
    }
}
