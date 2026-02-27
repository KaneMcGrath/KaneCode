using KaneCode.Models;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KaneCode.Controls;

/// <summary>
/// A panel that displays Roslyn diagnostics in a sortable data grid.
/// Double-clicking a row navigates to the source location.
/// </summary>
public partial class ErrorListPanel : UserControl
{
    public ErrorListPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The diagnostics collection displayed in the grid.
    /// </summary>
    public static readonly DependencyProperty DiagnosticsProperty =
        DependencyProperty.Register(
            nameof(Diagnostics),
            typeof(ObservableCollection<DiagnosticItem>),
            typeof(ErrorListPanel),
            new PropertyMetadata(null, OnDiagnosticsChanged));

    public ObservableCollection<DiagnosticItem>? Diagnostics
    {
        get => (ObservableCollection<DiagnosticItem>?)GetValue(DiagnosticsProperty);
        set => SetValue(DiagnosticsProperty, value);
    }

    /// <summary>
    /// Raised when the user double-clicks a diagnostic row to navigate to source.
    /// </summary>
    public event EventHandler<DiagnosticItem>? NavigateRequested;

    private static void OnDiagnosticsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ErrorListPanel panel)
        {
            panel.DiagnosticsGrid.ItemsSource = e.NewValue as ObservableCollection<DiagnosticItem>;
            panel.UpdateSummary();

            if (e.NewValue is ObservableCollection<DiagnosticItem> newCollection)
            {
                newCollection.CollectionChanged += (_, _) => panel.UpdateSummary();
            }
        }
    }

    private void UpdateSummary()
    {
        if (Diagnostics is null || Diagnostics.Count == 0)
        {
            SummaryText.Text = "No issues";
            return;
        }

        var errors = 0;
        var warnings = 0;
        var infos = 0;
        foreach (var d in Diagnostics)
        {
            switch (d.Severity)
            {
                case Microsoft.CodeAnalysis.DiagnosticSeverity.Error:
                    errors++;
                    break;
                case Microsoft.CodeAnalysis.DiagnosticSeverity.Warning:
                    warnings++;
                    break;
                case Microsoft.CodeAnalysis.DiagnosticSeverity.Info:
                    infos++;
                    break;
            }
        }

        var parts = new List<string>();
        if (errors > 0) parts.Add($"{errors} Error(s)");
        if (warnings > 0) parts.Add($"{warnings} Warning(s)");
        if (infos > 0) parts.Add($"{infos} Message(s)");
        SummaryText.Text = string.Join(", ", parts);
    }

    private void DiagnosticsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DiagnosticsGrid.SelectedItem is DiagnosticItem item)
        {
            NavigateRequested?.Invoke(this, item);
        }
    }

    private void ContextMenu_GoToSource(object sender, RoutedEventArgs e)
    {
        if (DiagnosticsGrid.SelectedItem is DiagnosticItem item)
        {
            NavigateRequested?.Invoke(this, item);
        }
    }

    private void ContextMenu_CopyMessage(object sender, RoutedEventArgs e)
    {
        if (DiagnosticsGrid.SelectedItem is DiagnosticItem item)
        {
            Clipboard.SetText($"{item.Code}: {item.Message}");
        }
    }

    private void ContextMenu_CopyLine(object sender, RoutedEventArgs e)
    {
        if (DiagnosticsGrid.SelectedItem is DiagnosticItem item)
        {
            Clipboard.SetText($"{item.SeverityIcon} {item.Code}: {item.Message} ({item.File}, line {item.Line}, col {item.Column})");
        }
    }
}
