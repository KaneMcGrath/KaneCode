using KaneCode.Models;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace KaneCode.Controls;

/// <summary>
/// Displays unstaged and staged Git file changes in two scrollable lists.
/// </summary>
public partial class GitChangesPanel : UserControl
{
    public GitChangesPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Files with unstaged working-directory changes.
    /// </summary>
    public static readonly DependencyProperty UnstagedChangesProperty =
        DependencyProperty.Register(
            nameof(UnstagedChanges),
            typeof(ObservableCollection<GitChangesEntry>),
            typeof(GitChangesPanel),
            new PropertyMetadata(null, OnUnstagedChangesChanged));

    public ObservableCollection<GitChangesEntry>? UnstagedChanges
    {
        get => (ObservableCollection<GitChangesEntry>?)GetValue(UnstagedChangesProperty);
        set => SetValue(UnstagedChangesProperty, value);
    }

    /// <summary>
    /// Files with staged index changes.
    /// </summary>
    public static readonly DependencyProperty StagedChangesProperty =
        DependencyProperty.Register(
            nameof(StagedChanges),
            typeof(ObservableCollection<GitChangesEntry>),
            typeof(GitChangesPanel),
            new PropertyMetadata(null, OnStagedChangesChanged));

    public ObservableCollection<GitChangesEntry>? StagedChanges
    {
        get => (ObservableCollection<GitChangesEntry>?)GetValue(StagedChangesProperty);
        set => SetValue(StagedChangesProperty, value);
    }

    /// <summary>
    /// Summary text shown in the panel header (e.g. "3 unstaged, 1 staged").
    /// </summary>
    public static readonly DependencyProperty PanelStatusTextProperty =
        DependencyProperty.Register(
            nameof(PanelStatusText),
            typeof(string),
            typeof(GitChangesPanel),
            new PropertyMetadata(string.Empty, OnPanelStatusTextChanged));

    public string PanelStatusText
    {
        get => (string)GetValue(PanelStatusTextProperty);
        set => SetValue(PanelStatusTextProperty, value);
    }

    /// <summary>Raised when the user clicks the Refresh button.</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>Raised when the user double-clicks a file entry to open it.</summary>
    public event EventHandler<GitChangesEntry>? FileOpenRequested;

    /// <summary>Raised when the user requests staging a single file.</summary>
    public event EventHandler<GitChangesEntry>? StageRequested;

    /// <summary>Raised when the user requests staging all unstaged files.</summary>
    public event EventHandler? StageAllRequested;

    /// <summary>Raised when the user requests unstaging a single file.</summary>
    public event EventHandler<GitChangesEntry>? UnstageRequested;

    /// <summary>Raised when the user requests unstaging all staged files.</summary>
    public event EventHandler? UnstageAllRequested;

    /// <summary>Raised when the user requests discarding changes to a single file.</summary>
    public event EventHandler<GitChangesEntry>? DiscardRequested;

    private static void OnUnstagedChangesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GitChangesPanel panel)
        {
            panel.UnstagedList.ItemsSource = e.NewValue as ObservableCollection<GitChangesEntry>;
            panel.UpdateSectionHeaders();

            if (e.OldValue is ObservableCollection<GitChangesEntry> old)
            {
                old.CollectionChanged -= panel.OnUnstagedCollectionChanged;
            }

            if (e.NewValue is ObservableCollection<GitChangesEntry> next)
            {
                next.CollectionChanged += panel.OnUnstagedCollectionChanged;
            }
        }
    }

    private static void OnStagedChangesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GitChangesPanel panel)
        {
            panel.StagedList.ItemsSource = e.NewValue as ObservableCollection<GitChangesEntry>;
            panel.UpdateSectionHeaders();

            if (e.OldValue is ObservableCollection<GitChangesEntry> old)
            {
                old.CollectionChanged -= panel.OnStagedCollectionChanged;
            }

            if (e.NewValue is ObservableCollection<GitChangesEntry> next)
            {
                next.CollectionChanged += panel.OnStagedCollectionChanged;
            }
        }
    }

    private static void OnPanelStatusTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GitChangesPanel panel)
        {
            panel.StatusText.Text = e.NewValue as string ?? string.Empty;
        }
    }

    private void OnUnstagedCollectionChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        UpdateSectionHeaders();

    private void OnStagedCollectionChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        UpdateSectionHeaders();

    private void UpdateSectionHeaders()
    {
        var unstagedCount = UnstagedChanges?.Count ?? 0;
        var stagedCount = StagedChanges?.Count ?? 0;
        UnstagedHeader.Text = $"Unstaged Changes ({unstagedCount})";
        StagedHeader.Text = $"Staged Changes ({stagedCount})";
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void StageAllButton_Click(object sender, RoutedEventArgs e)
    {
        StageAllRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UnstageAllButton_Click(object sender, RoutedEventArgs e)
    {
        UnstageAllRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UnstagedContextMenu_Stage(object sender, RoutedEventArgs e)
    {
        if (UnstagedList.SelectedItem is GitChangesEntry entry)
        {
            StageRequested?.Invoke(this, entry);
        }
    }

    private void UnstagedContextMenu_Discard(object sender, RoutedEventArgs e)
    {
        if (UnstagedList.SelectedItem is GitChangesEntry entry)
        {
            DiscardRequested?.Invoke(this, entry);
        }
    }

    private void StagedContextMenu_Unstage(object sender, RoutedEventArgs e)
    {
        if (StagedList.SelectedItem is GitChangesEntry entry)
        {
            UnstageRequested?.Invoke(this, entry);
        }
    }

    private void UnstagedList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (UnstagedList.SelectedItem is GitChangesEntry entry)
        {
            FileOpenRequested?.Invoke(this, entry);
        }
    }

    private void StagedList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (StagedList.SelectedItem is GitChangesEntry entry)
        {
            FileOpenRequested?.Invoke(this, entry);
        }
    }
}
