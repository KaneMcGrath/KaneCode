using System.Windows;

namespace KaneCode.Theming;

/// <summary>
/// Manages application themes by swapping resource dictionaries at runtime.
/// </summary>
internal static class ThemeManager
{
    private static readonly Uri DarkThemeUri = new("Themes/DarkTheme.xaml", UriKind.Relative);
    private static readonly Uri LightThemeUri = new("Themes/LightTheme.xaml", UriKind.Relative);

    private static ResourceDictionary? _currentThemeDictionary;

    /// <summary>
    /// Applies the specified theme to the application.
    /// </summary>
    public static void ApplyTheme(AppTheme theme)
    {
        var uri = theme switch
        {
            AppTheme.Light => LightThemeUri,
            _ => DarkThemeUri
        };

        var newDictionary = new ResourceDictionary { Source = uri };
        var appResources = Application.Current.Resources;

        if (_currentThemeDictionary is not null)
        {
            appResources.MergedDictionaries.Remove(_currentThemeDictionary);
        }

        appResources.MergedDictionaries.Add(newDictionary);
        _currentThemeDictionary = newDictionary;
    }

    /// <summary>
    /// Loads a custom theme from an arbitrary resource dictionary URI.
    /// </summary>
    public static void ApplyCustomTheme(Uri themeUri)
    {
        ArgumentNullException.ThrowIfNull(themeUri);

        var newDictionary = new ResourceDictionary { Source = themeUri };
        var appResources = Application.Current.Resources;

        if (_currentThemeDictionary is not null)
        {
            appResources.MergedDictionaries.Remove(_currentThemeDictionary);
        }

        appResources.MergedDictionaries.Add(newDictionary);
        _currentThemeDictionary = newDictionary;
    }
}

/// <summary>
/// Built-in theme options.
/// </summary>
internal enum AppTheme
{
    Dark,
    Light
}
