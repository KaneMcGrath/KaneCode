using KaneCode.Models;
using KaneCode.Services.Ai;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KaneCode.Controls;

/// <summary>
/// Displays AI debugging information in tabbed views.
/// </summary>
public partial class AiDebugPanel : UserControl
{
    private AiDebugLogService? _debugLogService;

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

    internal void SetDebugLogService(AiDebugLogService debugLogService)
    {
        ArgumentNullException.ThrowIfNull(debugLogService);
        _debugLogService = debugLogService;
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

    private void ToolFailuresGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DataGridRow? row = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is AiToolFailureEntry entry)
        {
            row.IsSelected = true;
            ToolFailuresGrid.SelectedItem = entry;
            ToolFailuresGrid.Focus();
        }
    }

    private void ContextMenu_OpenInTextEditor(object sender, RoutedEventArgs e)
    {
        if (ToolFailuresGrid.SelectedItem is not AiToolFailureEntry entry)
        {
            return;
        }

        try
        {
            string filePath = _debugLogService is null
                ? AiDebugLogService.ExportToolFailureEntry(entry, Path.GetTempPath())
                : _debugLogService.ExportToolFailureEntry(entry);

            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            });
        }
        catch (ArgumentException ex)
        {
            ShowExportError(ex.Message);
        }
        catch (IOException ex)
        {
            ShowExportError(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            ShowExportError(ex.Message);
        }
        catch (Win32Exception ex)
        {
            ShowExportError(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            ShowExportError(ex.Message);
        }
    }

    private void ShowExportError(string message)
    {
        Window? owner = Window.GetWindow(this);

        if (owner is null)
        {
            MessageBox.Show(message, "AI Debug", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        MessageBox.Show(owner, message, "AI Debug", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static T? FindVisualParent<T>(DependencyObject? dependencyObject)
        where T : DependencyObject
    {
        DependencyObject? current = dependencyObject;

        while (current is not null)
        {
            if (current is T parent)
            {
                return parent;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
