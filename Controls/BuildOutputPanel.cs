using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KaneCode.Models;

namespace KaneCode.Controls;

/// <summary>
/// A panel that displays build/run/test output. Each event is rendered in its own
/// read-only text area, and events are separated from one another by a horizontal rule.
/// </summary>
public partial class BuildOutputPanel : UserControl
{
    private readonly Dictionary<BuildOutputEvent, TextBox> _eventTextBoxes = [];

    public BuildOutputPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Raised when the user clicks Clear so the owner can reset its event state.
    /// </summary>
    public event EventHandler? ClearRequested;

    /// <summary>
    /// The collection of build/run/test events to display, one text area per event.
    /// </summary>
    public static readonly DependencyProperty OutputEventsProperty =
        DependencyProperty.Register(
            nameof(OutputEvents),
            typeof(ObservableCollection<BuildOutputEvent>),
            typeof(BuildOutputPanel),
            new PropertyMetadata(null, OnOutputEventsChanged));

    public ObservableCollection<BuildOutputEvent>? OutputEvents
    {
        get => (ObservableCollection<BuildOutputEvent>?)GetValue(OutputEventsProperty);
        set => SetValue(OutputEventsProperty, value);
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

    private static void OnOutputEventsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not BuildOutputPanel panel)
        {
            return;
        }

        if (e.OldValue is ObservableCollection<BuildOutputEvent> oldCollection)
        {
            oldCollection.CollectionChanged -= panel.OnEventsCollectionChanged;
        }

        if (e.NewValue is ObservableCollection<BuildOutputEvent> newCollection)
        {
            newCollection.CollectionChanged += panel.OnEventsCollectionChanged;
        }

        panel.RebuildAll();
    }

    private static void OnBuildSummaryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is BuildOutputPanel panel)
        {
            panel.SummaryText.Text = e.NewValue as string ?? string.Empty;
        }
    }

    private void OnEventsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                foreach (BuildOutputEvent outputEvent in e.NewItems.Cast<BuildOutputEvent>())
                {
                    AddEventSection(outputEvent);
                }

                OutputScroller.ScrollToEnd();
                break;

            case NotifyCollectionChangedAction.Reset:
                RebuildAll();
                break;

            default:
                // Remove/Replace/Move events (rare for this panel): rebuild from scratch.
                RebuildAll();
                break;
        }
    }

    private void RebuildAll()
    {
        foreach (KeyValuePair<BuildOutputEvent, TextBox> pair in _eventTextBoxes)
        {
            pair.Key.LineAppended -= OnEventLineAppended;
        }

        _eventTextBoxes.Clear();
        EventsPanel.Children.Clear();

        if (OutputEvents is not null)
        {
            foreach (BuildOutputEvent outputEvent in OutputEvents)
            {
                AddEventSection(outputEvent);
            }
        }

        OutputScroller.ScrollToEnd();
    }

    private void AddEventSection(BuildOutputEvent outputEvent)
    {
        // Horizontal rule separating this event from the previous one.
        if (EventsPanel.Children.Count > 0)
        {
            EventsPanel.Children.Add(new Border
            {
                Height = 1,
                Background = GetSeparatorBrush(),
                Margin = new Thickness(0, 4, 0, 4)
            });
        }

        var textBox = new TextBox
        {
            Text = outputEvent.Text,
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 12,
            Foreground = GetForegroundBrush(),
            Padding = new Thickness(8, 4, 8, 4),
            TextWrapping = TextWrapping.NoWrap,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            AcceptsReturn = true,
            AcceptsTab = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            IsReadOnlyCaretVisible = false
        };

        outputEvent.LineAppended += OnEventLineAppended;
        _eventTextBoxes[outputEvent] = textBox;
        EventsPanel.Children.Add(textBox);
    }

    private void OnEventLineAppended(BuildOutputEvent outputEvent, string line)
    {
        if (!_eventTextBoxes.TryGetValue(outputEvent, out TextBox? textBox))
        {
            return;
        }

        if (textBox.Text.Length > 0)
        {
            textBox.Text += Environment.NewLine;
        }

        textBox.Text += line;
        OutputScroller.ScrollToEnd();
    }

    private Brush GetSeparatorBrush() =>
        TryFindResource("ErrorListGridLine") as Brush ?? new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42));

    private Brush GetForegroundBrush() =>
        TryFindResource("ErrorListForeground") as Brush ?? Brushes.Gray;

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        OutputEvents?.Clear();
        ClearRequested?.Invoke(this, EventArgs.Empty);
    }
}
