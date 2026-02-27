using KaneCode.Models;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace KaneCode.Controls;

/// <summary>
/// A popup control that displays available Roslyn code actions (fixes and refactorings)
/// and lets the user select one to apply.
/// </summary>
public partial class CodeActionLightBulb : UserControl
{
    public CodeActionLightBulb()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Raised when the user selects a code action to apply.
    /// </summary>
    public event EventHandler<CodeActionItem>? ActionSelected;

    /// <summary>
    /// Shows the code action popup at the specified screen-relative position with the given items.
    /// </summary>
    public void Show(IReadOnlyList<CodeActionItem> items, UIElement placementTarget, double horizontalOffset, double verticalOffset)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            return;
        }

        ActionsList.ItemsSource = items;
        ActionPopup.PlacementTarget = placementTarget;
        ActionPopup.HorizontalOffset = horizontalOffset;
        ActionPopup.VerticalOffset = verticalOffset;
        ActionPopup.IsOpen = true;
        Visibility = Visibility.Visible;

        // Focus the list and select the first item
        ActionsList.SelectedIndex = 0;
        ActionsList.Focus();
    }

    /// <summary>
    /// Closes the popup if it is currently open.
    /// </summary>
    public void Close()
    {
        ActionPopup.IsOpen = false;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Returns true if the popup is currently open.
    /// </summary>
    public bool IsPopupOpen => ActionPopup.IsOpen;

    private void ActionsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ApplySelectedAction();
    }

    private void ActionsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            ApplySelectedAction();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void ActionPopup_Closed(object? sender, EventArgs e)
    {
        Visibility = Visibility.Collapsed;
    }

    private void ApplySelectedAction()
    {
        if (ActionsList.SelectedItem is CodeActionItem item)
        {
            Close();
            ActionSelected?.Invoke(this, item);
        }
    }
}
