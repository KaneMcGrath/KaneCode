using System.Windows;
using System.Windows.Controls;

namespace KaneCode.Theming;

/// <summary>
/// Attached properties for tracking password length on a <see cref="PasswordBox"/>
/// so that XAML triggers can show/hide a hint when the box is empty.
/// Ported from Theme.WPF (MIT licence).
/// </summary>
internal sealed class PasswordBoxHelper
{
    public static readonly DependencyProperty ListenToLengthProperty =
        DependencyProperty.RegisterAttached(
            "ListenToLength",
            typeof(bool),
            typeof(PasswordBoxHelper),
            new FrameworkPropertyMetadata(false, OnListenToLengthChanged));

    public static readonly DependencyProperty InputLengthProperty =
        DependencyProperty.RegisterAttached(
            "InputLength",
            typeof(int),
            typeof(PasswordBoxHelper),
            new FrameworkPropertyMetadata(0));

    public static bool GetListenToLength(PasswordBox box) =>
        (bool)box.GetValue(ListenToLengthProperty);

    public static void SetListenToLength(PasswordBox box, bool value) =>
        box.SetValue(ListenToLengthProperty, value);

    public static int GetInputLength(PasswordBox box) =>
        (int)box.GetValue(InputLengthProperty);

    public static void SetInputLength(PasswordBox box, int value) =>
        box.SetValue(InputLengthProperty, value);

    private static void OnListenToLengthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box)
        {
            return;
        }

        box.PasswordChanged -= OnPasswordChanged;
        if (e.NewValue is true)
        {
            box.PasswordChanged += OnPasswordChanged;
        }
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
        {
            SetInputLength(box, box.SecurePassword.Length);
        }
    }
}
