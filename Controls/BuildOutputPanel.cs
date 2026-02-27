using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace KaneCode.Controls;

/// <summary>
/// A panel that displays build/run process output as scrollable text.
/// </summary>
public partial class BuildOutputPanel : UserControl
{
    public BuildOutputPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The collection of output lines to display.
    /// </summary>
    public static readonly DependencyProperty OutputLinesProperty =
        DependencyProperty.Register(
            nameof(OutputLines),
            typeof(ObservableCollection<string>),
            typeof(BuildOutputPanel),
            new PropertyMetadata(null, OnOutputLinesChanged));

    public ObservableCollection<string>? OutputLines
    {
        get => (ObservableCollection<string>?)GetValue(OutputLinesProperty);
        set => SetValue(OutputLinesProperty, value);
    }

    /// <summary>
    /// Summary text shown in the header (e.g. "Build succeeded", "Build failed").
    /// </summary>
    public static readonly DependencyProperty BuildSummaryProperty =
        DependencyProperty.Register(
            nameof(BuildSummary),
            typeof(string),
            typeof(BuildOutputPanel),
            new PropertyMetadata(string.Empty, OnBuildSummaryChanged));

    public string BuildSummary
    {
        get => (string)GetValue(BuildSummaryProperty);
        set => SetValue(BuildSummaryProperty, value);
    }

    private static void OnOutputLinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not BuildOutputPanel panel)
        {
            return;
        }

        if (e.OldValue is ObservableCollection<string> oldCollection)
        {
            oldCollection.CollectionChanged -= panel.OnCollectionChanged;
        }

        if (e.NewValue is ObservableCollection<string> newCollection)
        {
            newCollection.CollectionChanged += panel.OnCollectionChanged;
            panel.RebuildText(newCollection);
        }
        else
        {
            panel.OutputText.Text = string.Empty;
        }
    }

    private static void OnBuildSummaryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is BuildOutputPanel panel)
        {
            panel.SummaryText.Text = e.NewValue as string ?? string.Empty;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            OutputText.Text = string.Empty;
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (string line in e.NewItems)
            {
                if (OutputText.Text.Length > 0)
                {
                    OutputText.Text += Environment.NewLine;
                }

                OutputText.Text += line;
            }

            // Auto-scroll to bottom
            OutputScroller.ScrollToEnd();
        }
    }

    private void RebuildText(ObservableCollection<string> lines)
    {
        OutputText.Text = string.Join(Environment.NewLine, lines);
        OutputScroller.ScrollToEnd();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        OutputLines?.Clear();
    }
}
