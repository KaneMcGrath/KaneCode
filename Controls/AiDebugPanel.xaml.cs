using KaneCode.Models;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace KaneCode.Controls;

/// <summary>
/// Displays AI debugging information in tabbed views.
/// </summary>
public partial class AiDebugPanel : UserControl
{
    public AiDebugPanel()
    {
        InitializeComponent();
        UpdateSummary();
    }

    public static readonly DependencyProperty ToolFailuresProperty =
        DependencyProperty.Register(
            nameof(ToolFailures),
            typeof(ObservableCollection<AiToolFailureEntry>),
            typeof(AiDebugPanel),
            new PropertyMetadata(null, OnToolFailuresChanged));

    public ObservableCollection<AiToolFailureEntry>? ToolFailures
    {
        get => (ObservableCollection<AiToolFailureEntry>?)GetValue(ToolFailuresProperty);
        set => SetValue(ToolFailuresProperty, value);
    }

    private static void OnToolFailuresChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not AiDebugPanel panel)
        {
            return;
        }

        if (e.OldValue is ObservableCollection<AiToolFailureEntry> oldCollection)
        {
            oldCollection.CollectionChanged -= panel.ToolFailures_CollectionChanged;
        }

        if (e.NewValue is ObservableCollection<AiToolFailureEntry> newCollection)
        {
            newCollection.CollectionChanged += panel.ToolFailures_CollectionChanged;
            panel.ToolFailuresGrid.ItemsSource = newCollection;
        }
        else
        {
            panel.ToolFailuresGrid.ItemsSource = null;
        }

        panel.UpdateSummary();
    }

    private void ToolFailures_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int count = ToolFailures?.Count ?? 0;

        SummaryText.Text = count switch
        {
            0 => "No tool failures",
            1 => "1 tool failure",
            _ => $"{count} tool failures"
        };
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        ToolFailures?.Clear();
    }
}
