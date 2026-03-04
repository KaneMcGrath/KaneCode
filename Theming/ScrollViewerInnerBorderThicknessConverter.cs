using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace KaneCode.Theming;

/// <summary>
/// Calculates a <see cref="Thickness"/> for an inner border based on whether
/// the horizontal and vertical scroll bars are visible.
/// Ported from the Theme.WPF library (MIT).
/// </summary>
internal sealed class ScrollViewerInnerBorderThicknessConverter : IMultiValueConverter
{
    public static ScrollViewerInnerBorderThicknessConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not { Length: 2 })
        {
            return new Thickness(0);
        }

        var bottomBarVisible = values[0] is Visibility v0 && v0 == Visibility.Visible;
        var rightBarVisible = values[1] is Visibility v1 && v1 == Visibility.Visible;

        return new Thickness(
            0,
            0,
            rightBarVisible ? 1.0 : 0.0,
            bottomBarVisible ? 1.0 : 0.0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
