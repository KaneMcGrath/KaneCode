using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using AvalonDock.Themes;

namespace KaneCode.Theming;

/// <summary>
/// Manages the list of available themes and performs runtime switching of
/// MLib base resources, application brush overrides, and the AvalonDock theme.
/// </summary>
public sealed class ThemeManager : INotifyPropertyChanged
{
    /// <summary>
    /// Index of the MLib base theme dictionary in
    /// <see cref="Application.Current"/> Resources.MergedDictionaries.
    /// </summary>
    private const int MLibBaseIndex = 0;

    /// <summary>
    /// Index of the application extended brushes dictionary in
    /// <see cref="Application.Current"/> Resources.MergedDictionaries.
    /// </summary>
    private const int AppBrushesIndex = 1;

    private ThemeOption _currentTheme;

    public ThemeManager()
    {
        AvailableThemes =
        [
            new ThemeOption(
                "Dark",
                new Uri("pack://application:,,,/MLib;component/Themes/DarkTheme.xaml"),
                new Uri("/KaneCode;component/Themes/DarkBrushes.xaml", UriKind.RelativeOrAbsolute),
                new Vs2013DarkTheme()),

            new ThemeOption(
                "Light",
                new Uri("pack://application:,,,/MLib;component/Themes/LightTheme.xaml"),
                new Uri("/KaneCode;component/Themes/LightBrushes.xaml", UriKind.RelativeOrAbsolute),
                new Vs2013LightTheme()),

            new ThemeOption(
                "Blue",
                new Uri("pack://application:,,,/MLib;component/Themes/LightTheme.xaml"),
                new Uri("/KaneCode;component/Themes/BlueBrushes.xaml", UriKind.RelativeOrAbsolute),
                new Vs2013BlueTheme()),
        ];

        // Default to the first theme without triggering a resource swap
        // (App.xaml already loads these resources at startup).
        _currentTheme = AvailableThemes[0];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised after a theme switch so that components (e.g. the editor) can refresh.
    /// </summary>
    public event Action? ThemeChanged;

    /// <summary>
    /// All themes the user can choose from.
    /// </summary>
    public IReadOnlyList<ThemeOption> AvailableThemes { get; }

    /// <summary>
    /// The currently active theme. Setting this property swaps all theme resources at runtime.
    /// </summary>
    public ThemeOption CurrentTheme
    {
        get => _currentTheme;
        set
        {
            if (_currentTheme == value)
                return;

            _currentTheme = value;
            ApplyTheme(value);
            OnPropertyChanged();
            ThemeChanged?.Invoke();
        }
    }

    /// <summary>
    /// Swap MLib base and app brush dictionaries by index.
    /// </summary>
    private static void ApplyTheme(ThemeOption theme)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        dictionaries[MLibBaseIndex].Source = theme.MLibBaseUri;
        dictionaries[AppBrushesIndex].Source = theme.AppBrushesUri;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
