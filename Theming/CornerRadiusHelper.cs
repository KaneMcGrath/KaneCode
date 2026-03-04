using System.Windows;

namespace KaneCode.Theming;

/// <summary>
/// Attached property that lets XAML control templates bind a CornerRadius
/// via <c>TemplateBinding</c>. Ported from the Theme.WPF library (MIT).
/// </summary>
internal static class CornerRadiusHelper
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.RegisterAttached(
            "Value",
            typeof(CornerRadius),
            typeof(CornerRadiusHelper),
            new PropertyMetadata(new CornerRadius(0)));

    public static void SetValue(DependencyObject element, CornerRadius value) =>
        element.SetValue(ValueProperty, value);

    public static CornerRadius GetValue(DependencyObject element) =>
        (CornerRadius)element.GetValue(ValueProperty);
}
