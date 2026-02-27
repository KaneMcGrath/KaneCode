using KaneCode.Theming;
using KaneCode.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KaneCode;

/// <summary>
/// Options dialog with categorized settings pages (General, Appearance, Hotkeys).
/// </summary>
public partial class OptionsWindow : Window
{
    private bool _isInitializing = true;
    private readonly HotkeySettingsViewModel _hotkeyViewModel = new();

    public OptionsWindow()
    {
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

        _isInitializing = false;
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
}
