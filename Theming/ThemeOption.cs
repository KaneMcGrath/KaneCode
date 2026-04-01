using AvalonDock.Themes;

namespace KaneCode.Theming;

/// <summary>
/// Represents a single theme configuration combining MLib base resources,
/// application-specific brush overrides, and the AvalonDock theme object.
/// </summary>
/// <param name="DisplayName">User-facing name shown in the theme selector.</param>
/// <param name="MLibBaseUri">Pack URI to the MLib base theme (e.g. DarkTheme.xaml or LightTheme.xaml).</param>
/// <param name="AppBrushesUri">Relative URI to the app-level extended brushes XAML.</param>
/// <param name="AvalonDockTheme">AvalonDock VS2013 theme instance applied to the DockingManager.</param>
public sealed record ThemeOption(
    string DisplayName,
    Uri MLibBaseUri,
    Uri AppBrushesUri,
    Theme AvalonDockTheme)
{
    public override string ToString() => DisplayName;
}
