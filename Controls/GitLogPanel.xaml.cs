using KaneCode.Models;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace KaneCode.Controls;

/// <summary>
/// Displays commit history rows in a sortable grid.
/// </summary>
public partial class GitLogPanel : UserControl
{
    public GitLogPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Commit rows shown in the grid.
    /// </summary>
    public static readonly DependencyProperty CommitsProperty =
        DependencyProperty.Register(
            nameof(Commits),
            typeof(ObservableCollection<GitCommitLogItem>),
            typeof(GitLogPanel),
            new PropertyMetadata(null, OnCommitsChanged));

    public ObservableCollection<GitCommitLogItem>? Commits
    {
        get => (ObservableCollection<GitCommitLogItem>?)GetValue(CommitsProperty);
        set => SetValue(CommitsProperty, value);
    }

    /// <summary>
    /// Status text shown in the panel header.
    /// </summary>
    public static readonly DependencyProperty PanelStatusTextProperty =
        DependencyProperty.Register(
            nameof(PanelStatusText),
            typeof(string),
            typeof(GitLogPanel),
            new PropertyMetadata(string.Empty, OnPanelStatusTextChanged));

    public string PanelStatusText
    {
        get => (string)GetValue(PanelStatusTextProperty);
        set => SetValue(PanelStatusTextProperty, value);
    }

    private static void OnCommitsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GitLogPanel panel)
        {
            panel.CommitGrid.ItemsSource = e.NewValue as ObservableCollection<GitCommitLogItem>;
        }
    }

    private static void OnPanelStatusTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GitLogPanel panel)
        {
            panel.StatusText.Text = e.NewValue as string ?? string.Empty;
        }
    }
}
