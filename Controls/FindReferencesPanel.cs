using KaneCode.Models;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KaneCode.Controls;

/// <summary>
/// A panel that displays Roslyn symbol references in a sortable data grid.
/// Double-clicking a row navigates to the reference location.
/// </summary>
public partial class FindReferencesPanel : UserControl
{
    public FindReferencesPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The references collection displayed in the grid.
    /// </summary>
    public static readonly DependencyProperty ReferencesProperty =
        DependencyProperty.Register(
            nameof(References),
            typeof(ObservableCollection<ReferenceItem>),
            typeof(FindReferencesPanel),
            new PropertyMetadata(null, OnReferencesChanged));

    public ObservableCollection<ReferenceItem>? References
    {
        get => (ObservableCollection<ReferenceItem>?)GetValue(ReferencesProperty);
        set => SetValue(ReferencesProperty, value);
    }

    /// <summary>
    /// Status text shown in the header (e.g., symbol name and match count).
    /// </summary>
    public static readonly DependencyProperty PanelStatusTextProperty =
        DependencyProperty.Register(
            nameof(PanelStatusText),
            typeof(string),
            typeof(FindReferencesPanel),
            new PropertyMetadata(string.Empty, OnPanelStatusTextChanged));

    public string PanelStatusText
    {
        get => (string)GetValue(PanelStatusTextProperty);
        set => SetValue(PanelStatusTextProperty, value);
    }

    /// <summary>
    /// Raised when the user double-clicks a reference row to navigate to source.
    /// </summary>
    public event EventHandler<ReferenceItem>? NavigateRequested;

    private static void OnReferencesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FindReferencesPanel panel)
        {
            panel.ReferencesGrid.ItemsSource = e.NewValue as ObservableCollection<ReferenceItem>;
        }
    }

    private static void OnPanelStatusTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FindReferencesPanel panel)
        {
            panel.StatusText.Text = e.NewValue as string ?? string.Empty;
        }
    }

    private void ReferencesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ReferencesGrid.SelectedItem is ReferenceItem item)
        {
            NavigateRequested?.Invoke(this, item);
        }
    }

    private void ContextMenu_GoToReference(object sender, RoutedEventArgs e)
    {
        if (ReferencesGrid.SelectedItem is ReferenceItem item)
        {
            NavigateRequested?.Invoke(this, item);
        }
    }

    private void ContextMenu_CopyPath(object sender, RoutedEventArgs e)
    {
        if (ReferencesGrid.SelectedItem is ReferenceItem item)
        {
            Clipboard.SetText(item.FilePath);
        }
    }

    private void ContextMenu_CopyLine(object sender, RoutedEventArgs e)
    {
        if (ReferencesGrid.SelectedItem is ReferenceItem item)
        {
            Clipboard.SetText($"{item.FileName}({item.Line},{item.Column}): {item.Preview}");
        }
    }
}
