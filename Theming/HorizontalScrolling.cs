using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace KaneCode.Theming;

/// <summary>
/// Attached property that enables horizontal scrolling via Shift+MouseWheel
/// on any UIElement that contains a <see cref="ScrollViewer"/>.
/// Ported from Theme.WPF (MIT licence).
/// </summary>
internal static class HorizontalScrolling
{
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(int nAction, int nParam, ref int value, int ignore);

    private static bool _hasCachedScrollChars;
    private static int _scrollChars;

    private static int ScrollChars
    {
        get
        {
            if (_hasCachedScrollChars)
            {
                return _scrollChars;
            }

            if (!SystemParametersInfo(108, 0, ref _scrollChars, 0))
            {
                throw new Win32Exception();
            }

            _hasCachedScrollChars = true;
            return _scrollChars;
        }
    }

    public static readonly DependencyProperty UseHorizontalScrollingProperty =
        DependencyProperty.RegisterAttached(
            "UseHorizontalScrolling",
            typeof(bool),
            typeof(HorizontalScrolling),
            new PropertyMetadata(false, OnUseHorizontalScrollingChanged));

    public static readonly DependencyProperty IsRequireShiftForHorizontalScrollProperty =
        DependencyProperty.RegisterAttached(
            "IsRequireShiftForHorizontalScroll",
            typeof(bool),
            typeof(HorizontalScrolling),
            new PropertyMetadata(true));

    public static readonly DependencyProperty ForceHorizontalScrollingProperty =
        DependencyProperty.RegisterAttached(
            "ForceHorizontalScrolling",
            typeof(bool),
            typeof(HorizontalScrolling),
            new PropertyMetadata(false));

    public static readonly DependencyProperty HorizontalScrollingAmountProperty =
        DependencyProperty.RegisterAttached(
            "HorizontalScrollingAmount",
            typeof(int),
            typeof(HorizontalScrolling),
            new PropertyMetadata(ScrollChars));

    public static void SetUseHorizontalScrolling(DependencyObject element, bool value) =>
        element.SetValue(UseHorizontalScrollingProperty, value);
    public static bool GetUseHorizontalScrolling(DependencyObject element) =>
        (bool)element.GetValue(UseHorizontalScrollingProperty);

    public static void SetIsRequireShiftForHorizontalScroll(DependencyObject element, bool value) =>
        element.SetValue(IsRequireShiftForHorizontalScrollProperty, value);
    public static bool GetIsRequireShiftForHorizontalScroll(DependencyObject element) =>
        (bool)element.GetValue(IsRequireShiftForHorizontalScrollProperty);

    public static bool GetForceHorizontalScrolling(DependencyObject d) =>
        (bool)d.GetValue(ForceHorizontalScrollingProperty);
    public static void SetForceHorizontalScrolling(DependencyObject d, bool value) =>
        d.SetValue(ForceHorizontalScrollingProperty, value);

    public static int GetHorizontalScrollingAmount(DependencyObject d) =>
        (int)d.GetValue(HorizontalScrollingAmountProperty);
    public static void SetHorizontalScrollingAmount(DependencyObject d, int value) =>
        d.SetValue(HorizontalScrollingAmountProperty, value);

    private static void OnUseHorizontalScrollingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.PreviewMouseWheel -= OnPreviewMouseWheel;
        if (e.NewValue is true)
        {
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not UIElement element || e.Delta == 0)
        {
            return;
        }

        var scroller = FindVisualChild<ScrollViewer>(element);
        if (scroller is null)
        {
            return;
        }

        if (GetIsRequireShiftForHorizontalScroll(element) &&
            scroller.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled)
        {
            return;
        }

        var amount = Math.Max(1, GetHorizontalScrollingAmount(element));

        if (Keyboard.Modifiers == ModifierKeys.Shift ||
            Mouse.MiddleButton == MouseButtonState.Pressed ||
            GetForceHorizontalScrolling(element))
        {
            var count = (e.Delta / 120) * amount;
            if (e.Delta < 0)
            {
                for (var i = -count; i > 0; i--)
                {
                    scroller.LineRight();
                }
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    scroller.LineLeft();
                }
            }

            e.Handled = true;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject obj, bool includeSelf = true) where T : class
    {
        if (includeSelf && obj is T t)
        {
            return t;
        }

        return FindVisualChildInternal<T>(obj);
    }

    private static T? FindVisualChildInternal<T>(DependencyObject obj) where T : class
    {
        if (obj is ContentControl cc && cc.Content is DependencyObject contentChild)
        {
            return contentChild is T t ? t : FindVisualChildInternal<T>(contentChild);
        }

        if ((obj is Visual or Visual3D) && VisualTreeHelper.GetChildrenCount(obj) is var count and > 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (VisualTreeHelper.GetChild(obj, i) is T t)
                {
                    return t;
                }
            }

            for (var i = 0; i < count; i++)
            {
                var child = FindVisualChildInternal<T>(VisualTreeHelper.GetChild(obj, i));
                if (child is not null)
                {
                    return child;
                }
            }
        }

        return null;
    }
}
