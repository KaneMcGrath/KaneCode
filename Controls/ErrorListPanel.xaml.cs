using KaneCode.Models;
using Microsoft.CodeAnalysis;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace KaneCode.Controls;

/// <summary>
/// A panel that displays Roslyn diagnostics in a sortable, filterable data grid.
/// Double-clicking a row navigates to the source location.
/// </summary>
public partial class ErrorListPanel : UserControl, INotifyPropertyChanged
{
    private bool _showErrors = true;
    private bool _showWarnings = true;
    private bool _showMessages = true;
    private string _groupByProperty = "";

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

    /// <summary>Whether error-severity diagnostics are shown.</summary>
    public bool ShowErrors
    {
        get => _showErrors;
        set { _showErrors = value; OnPropertyChanged(nameof(ShowErrors)); RefreshFilter(); }
    }

    /// <summary>Whether warning-severity diagnostics are shown.</summary>
    public bool ShowWarnings
    {
        get => _showWarnings;
        set { _showWarnings = value; OnPropertyChanged(nameof(ShowWarnings)); RefreshFilter(); }
    }

    /// <summary>Whether info-severity diagnostics are shown.</summary>
    public bool ShowMessages
    {
        get => _showMessages;
        set { _showMessages = value; OnPropertyChanged(nameof(ShowMessages)); RefreshFilter(); }
    }

    /// <summary>
    /// Raised when the user double-clicks a diagnostic row to navigate to source.
    /// </summary>
    public event EventHandler<DiagnosticItem>? NavigateRequested;

    /// <summary>
    /// Raised when the user clicks the "Fix" link on a diagnostic row.
    /// </summary>
    public event EventHandler<DiagnosticItem>? FixRequested;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static void OnDiagnosticsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ErrorListPanel panel)
        {
            if (e.OldValue is ObservableCollection<DiagnosticItem> oldCollection)
            {
                oldCollection.CollectionChanged -= panel.OnCollectionChanged;
            }

            panel.ApplyViewSource();

            if (e.NewValue is ObservableCollection<DiagnosticItem> newCollection)
            {
                newCollection.CollectionChanged += panel.OnCollectionChanged;
            }
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshFilter();
        UpdateSummary();
    }

    private void ApplyViewSource()
    {
        if (Diagnostics is null)
        {
            DiagnosticsGrid.ItemsSource = null;
            UpdateSummary();
            return;
        }

        ICollectionView view = CollectionViewSource.GetDefaultView(Diagnostics);
        view.Filter = FilterDiagnostic;
        DiagnosticsGrid.ItemsSource = view;
        UpdateSummary();
    }

    private void RefreshFilter()
    {
        if (Diagnostics is null)
        {
            return;
        }

        ICollectionView view = CollectionViewSource.GetDefaultView(Diagnostics);
        view.Refresh();
        UpdateSummary();
    }

    /// <summary>
    /// Determines whether a diagnostic should be shown based on severity filters.
    /// Instance wrapper used by the ICollectionView filter.
    /// </summary>
    internal bool FilterDiagnostic(object obj)
    {
        return ShouldShowDiagnostic(obj, _showErrors, _showWarnings, _showMessages);
    }

    /// <summary>
    /// Pure filter predicate that checks severity against the provided toggle flags.
    /// Static so it can be unit-tested without instantiating the WPF control.
    /// </summary>
    internal static bool ShouldShowDiagnostic(object obj, bool showErrors, bool showWarnings, bool showMessages)
    {
        if (obj is not DiagnosticItem item)
        {
            return false;
        }

        return item.Severity switch
        {
            DiagnosticSeverity.Error => showErrors,
            DiagnosticSeverity.Warning => showWarnings,
            DiagnosticSeverity.Info => showMessages,
            _ => true
        };
    }

    private void UpdateSummary()
    {
        if (Diagnostics is null || Diagnostics.Count == 0)
        {
            SummaryText.Text = "No issues";
            return;
        }

        int errors = 0;
        int warnings = 0;
        int infos = 0;
        foreach (DiagnosticItem d in Diagnostics)
        {
            switch (d.Severity)
            {
                case DiagnosticSeverity.Error:
                    errors++;
                    break;
                case DiagnosticSeverity.Warning:
                    warnings++;
                    break;
                case DiagnosticSeverity.Info:
                    infos++;
                    break;
            }
        }

        List<string> parts = [];
        if (errors > 0) parts.Add($"{errors} Error(s)");
        if (warnings > 0) parts.Add($"{warnings} Warning(s)");
        if (infos > 0) parts.Add($"{infos} Message(s)");
        SummaryText.Text = string.Join(", ", parts);
    }

    private void GroupBy_Click(object sender, RoutedEventArgs e)
    {
        if (Diagnostics is null)
        {
            return;
        }

        if (sender is not MenuItem menuItem || menuItem.Tag is not string property)
        {
            return;
        }

        ICollectionView view = CollectionViewSource.GetDefaultView(Diagnostics);
        view.GroupDescriptions.Clear();

        if (string.Equals(property, _groupByProperty, StringComparison.Ordinal))
        {
            _groupByProperty = "";
            return;
        }

        _groupByProperty = property;
        view.GroupDescriptions.Add(new PropertyGroupDescription(property));
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

    private void FixLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is DiagnosticItem item)
        {
            FixRequested?.Invoke(this, item);
        }
    }
}
