using KaneCode.Theming;
using System.Windows;
using System.Windows.Controls;

namespace KaneCode;

/// <summary>
/// Options dialog with categorized settings pages (General, Appearance, Hotkeys).
/// </summary>
public partial class OptionsWindow : Window
{
    private bool _isInitializing = true;

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
}
