using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace KaneCode.Theming;

/// <summary>
/// Attached property for showing a hint when a text box is empty.
/// Set the control's Tag property to the hint text.
/// Ported from Theme.WPF (MIT licence).
/// </summary>
internal static class TextHinting
{
    public static readonly DependencyProperty ShowWhenFocusedProperty =
        DependencyProperty.RegisterAttached(
            "ShowWhenFocused",
            typeof(bool),
            typeof(TextHinting),
            new FrameworkPropertyMetadata(false));

    public static void SetShowWhenFocused(DependencyObject element, bool value) =>
        element.SetValue(ShowWhenFocusedProperty, value);

    public static bool GetShowWhenFocused(DependencyObject element) =>
        (bool)element.GetValue(ShowWhenFocusedProperty);
}
