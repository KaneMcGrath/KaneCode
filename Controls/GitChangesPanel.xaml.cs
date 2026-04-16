using KaneCode.Models;
using KaneCode.ViewModels;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KaneCode.Controls;

/// <summary>
/// Displays unstaged and staged Git file changes in two scrollable lists.
/// </summary>
public partial class GitChangesPanel : UserControl
{
    public GitChangesPanel()
    {
        InitializeComponent();
        UpdatePanelLayout();
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

    /// <summary>
    /// Local branch names shown in the branch selector.
    /// </summary>
    public static readonly DependencyProperty GitBranchesProperty =
        DependencyProperty.Register(
            nameof(GitBranches),
            typeof(ObservableCollection<string>),
            typeof(GitChangesPanel),
            new PropertyMetadata(null, OnGitBranchesChanged));

    public ObservableCollection<string>? GitBranches
    {
        get => (ObservableCollection<string>?)GetValue(GitBranchesProperty);
        set => SetValue(GitBranchesProperty, value);
    }

    /// <summary>
    /// Currently selected branch in the branch selector.
    /// </summary>
    public static readonly DependencyProperty SelectedGitBranchProperty =
        DependencyProperty.Register(
            nameof(SelectedGitBranch),
            typeof(string),
            typeof(GitChangesPanel),
            new PropertyMetadata(null, OnSelectedGitBranchChanged));

    public string? SelectedGitBranch
    {
        get => (string?)GetValue(SelectedGitBranchProperty);
        set => SetValue(SelectedGitBranchProperty, value);
    }

    /// <summary>
    /// Indicates whether the current workspace is backed by an open Git repository.
    /// </summary>
    public static readonly DependencyProperty IsRepositoryOpenProperty =
        DependencyProperty.Register(
            nameof(IsRepositoryOpen),
            typeof(bool),
            typeof(GitChangesPanel),
            new PropertyMetadata(false, OnIsRepositoryOpenChanged));

    public bool IsRepositoryOpen
    {
        get => (bool)GetValue(IsRepositoryOpenProperty);
        set => SetValue(IsRepositoryOpenProperty, value);
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

    /// <summary>Raised when the user requests opening side-by-side diff for a file.</summary>
    public event EventHandler<GitChangesEntry>? DiffRequested;

    /// <summary>Raised when the user chooses "Accept Current" conflict resolution for a file.</summary>
    public event EventHandler<GitChangesEntry>? AcceptCurrentConflictRequested;

    /// <summary>Raised when the user chooses "Accept Incoming" conflict resolution for a file.</summary>
    public event EventHandler<GitChangesEntry>? AcceptIncomingConflictRequested;

    /// <summary>Raised when the user chooses "Accept Both" conflict resolution for a file.</summary>
    public event EventHandler<GitChangesEntry>? AcceptBothConflictRequested;

    private static void OnUnstagedChangesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GitChangesPanel panel)
        {
            panel.UnstagedList.ItemsSource = e.NewValue as ObservableCollection<GitChangesEntry>;
            panel.UpdatePanelLayout();

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
            panel.UpdatePanelLayout();

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

    private static void OnGitBranchesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GitChangesPanel panel)
        {
            panel.BranchCombo.ItemsSource = e.NewValue as ObservableCollection<string>;
        }
    }

    private static void OnSelectedGitBranchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GitChangesPanel panel)
        {
            panel.BranchCombo.SelectedItem = e.NewValue as string;
        }
    }

    private static void OnIsRepositoryOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GitChangesPanel panel)
        {
            panel.UpdatePanelLayout();
        }
    }

    private void OnUnstagedCollectionChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        UpdatePanelLayout();

    private void OnStagedCollectionChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        UpdatePanelLayout();

    private void UpdateSectionHeaders()
    {
        int unstagedCount = UnstagedChanges?.Count ?? 0;
        int stagedCount = StagedChanges?.Count ?? 0;

        if (stagedCount > 0)
        {
            UnstagedHeader.Text = $"Unstaged Changes ({unstagedCount})";
            StagedHeader.Text = $"Staged Changes ({stagedCount})";
            return;
        }

        int totalCount = unstagedCount + stagedCount;
        UnstagedHeader.Text = $"Changes ({totalCount})";
        StagedHeader.Text = "Staged Changes (0)";
    }

    private void UpdatePanelLayout()
    {
        UpdateSectionHeaders();

        bool hasRepository = IsRepositoryOpen;
        bool hasStagedChanges = (StagedChanges?.Count ?? 0) > 0;

        RepositoryContent.Visibility = hasRepository ? Visibility.Visible : Visibility.Collapsed;
        NoRepositoryState.Visibility = hasRepository ? Visibility.Collapsed : Visibility.Visible;
        BranchSelectorPanel.Visibility = hasRepository ? Visibility.Visible : Visibility.Collapsed;
        CommitPanel.Visibility = hasRepository ? Visibility.Visible : Visibility.Collapsed;
        SplitterRow.Height = hasStagedChanges ? new GridLength(4) : new GridLength(0);
        StagedSectionRow.Height = hasStagedChanges ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        StagedSplitter.Visibility = hasStagedChanges ? Visibility.Visible : Visibility.Collapsed;
        StagedSection.Visibility = hasStagedChanges ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void BranchCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedGitBranch = BranchCombo.SelectedItem as string;
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

    private void UnstagedContextMenu_ViewDiff(object sender, RoutedEventArgs e)
    {
        if (UnstagedList.SelectedItem is GitChangesEntry entry)
        {
            DiffRequested?.Invoke(this, entry);
        }
    }

    private void UnstagedContextMenu_AcceptCurrent(object sender, RoutedEventArgs e)
    {
        if (UnstagedList.SelectedItem is GitChangesEntry entry)
        {
            AcceptCurrentConflictRequested?.Invoke(this, entry);
        }
    }

    private void UnstagedContextMenu_AcceptIncoming(object sender, RoutedEventArgs e)
    {
        if (UnstagedList.SelectedItem is GitChangesEntry entry)
        {
            AcceptIncomingConflictRequested?.Invoke(this, entry);
        }
    }

    private void UnstagedContextMenu_AcceptBoth(object sender, RoutedEventArgs e)
    {
        if (UnstagedList.SelectedItem is GitChangesEntry entry)
        {
            AcceptBothConflictRequested?.Invoke(this, entry);
        }
    }

    private void StagedContextMenu_Unstage(object sender, RoutedEventArgs e)
    {
        if (StagedList.SelectedItem is GitChangesEntry entry)
        {
            UnstageRequested?.Invoke(this, entry);
        }
    }

    private void StagedContextMenu_ViewDiff(object sender, RoutedEventArgs e)
    {
        if (StagedList.SelectedItem is GitChangesEntry entry)
        {
            DiffRequested?.Invoke(this, entry);
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

    private void CommitMessageBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Allow Ctrl+Enter to trigger commit (with multi-line support)
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            if (DataContext is MainViewModel vm)
            {
                vm.CommitGitChangesCommand?.Execute(null);
            }
        }
    }
}
