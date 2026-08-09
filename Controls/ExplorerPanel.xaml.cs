using KaneCode.Models;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace KaneCode.Controls;

/// <summary>Operations the explorer panel can request from its host window.</summary>
public enum ExplorerAction
{
    /// <summary>Open the given items (files open in the editor, folders toggle).</summary>
    Open,
    /// <summary>Delete the given items from disk.</summary>
    Delete,
    /// <summary>Rename the primary item.</summary>
    Rename,
    /// <summary>Create a new folder under the primary item (or the project root).</summary>
    NewFolder,
    /// <summary>Create a new blank file under the primary item (or the project root).</summary>
    NewBlankFile,
    /// <summary>Create a new file from the named template.</summary>
    NewFileFromTemplate,
    /// <summary>Rebuild the explorer tree.</summary>
    Refresh,
    /// <summary>Ask the host to supply the list of file templates.</summary>
    RequestTemplates
}

/// <summary>Event args for <see cref="ExplorerPanel.ExplorerActionRequested"/>.</summary>
public sealed class ExplorerActionEventArgs : RoutedEventArgs
{
    public ExplorerActionEventArgs(ExplorerAction action, IReadOnlyList<ProjectItem> items, string? templateName = null)
    {
        Action = action;
        Items = items;
        TemplateName = templateName;
    }

    public ExplorerAction Action { get; }

    public IReadOnlyList<ProjectItem> Items { get; }

    /// <summary>Template name for <see cref="ExplorerAction.NewFileFromTemplate"/>.</summary>
    public string? TemplateName { get; }

    /// <summary>Populated by the host in response to <see cref="ExplorerAction.RequestTemplates"/>.</summary>
    public IReadOnlyList<FileTemplate>? Templates { get; set; }
}

/// <summary>
/// Modern project explorer with multi-selection, keyboard navigation, filtering,
/// drag-to-AI-chat, and context actions that operate on the whole selection.
/// </summary>
public partial class ExplorerPanel : UserControl
{
    public static readonly RoutedEvent ExplorerActionRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(ExplorerActionRequested),
        RoutingStrategy.Bubble,
        typeof(EventHandler<ExplorerActionEventArgs>),
        typeof(ExplorerPanel));

    /// <summary>Raised whenever the panel needs the host to perform a filesystem action.</summary>
    public event EventHandler<ExplorerActionEventArgs> ExplorerActionRequested
    {
        add => AddHandler(ExplorerActionRequestedEvent, value);
        remove => RemoveHandler(ExplorerActionRequestedEvent, value);
    }

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(IEnumerable),
        typeof(ExplorerPanel),
        new FrameworkPropertyMetadata(null, OnItemsSourceChanged));

    /// <summary>The root tree nodes (bound to the view model's <c>ProjectItems</c>).</summary>
    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    private readonly List<ProjectItem> _selectedItems = [];
    private ProjectItem? _primaryItem;
    private ProjectItem? _anchorItem;
    private Point _dragStartPoint;
    private bool _isDragging;

    // Manual double-click detection (the panel handles mouse-down itself for
    // multi-select, so the built-in MouseDoubleClick event never fires).
    private DateTime _lastClickTime;
    private Point _lastClickPosition;
    private ProjectItem? _lastClickItem;

    private static readonly TimeSpan DoubleClickTime = TimeSpan.FromMilliseconds(500);
    private const double DoubleClickTolerance = 6.0;

    public ExplorerPanel()
    {
        InitializeComponent();
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ExplorerPanel)d).OnItemsSourceChangedCore();
    }

    private void OnItemsSourceChangedCore()
    {
        ClearSelection();
        ApplyFilter();
        UpdateStatusText();
    }

    // ── Tree helpers ─────────────────────────────────────────────────

    private IEnumerable<ProjectItem> GetRootItems() =>
        ItemsSource is IEnumerable source
            ? source.OfType<ProjectItem>()
            : Enumerable.Empty<ProjectItem>();

    private List<ProjectItem> FlattenVisibleItems() => FlattenVisibleItems(GetRootItems());

    /// <summary>Depth-first list of items whose ancestors are all expanded and visible.</summary>
    internal static List<ProjectItem> FlattenVisibleItems(IEnumerable<ProjectItem> roots)
    {
        var result = new List<ProjectItem>();
        foreach (var root in roots)
        {
            FlattenVisibleItems(root, result);
        }
        return result;
    }

    private static void FlattenVisibleItems(ProjectItem item, List<ProjectItem> result)
    {
        if (!item.IsVisible)
        {
            return;
        }

        result.Add(item);
        if (item.IsExpanded)
        {
            foreach (var child in item.Children)
            {
                FlattenVisibleItems(child, result);
            }
        }
    }

    private int TotalItemCount() => GetRootItems().Sum(CountItems);

    private static int CountItems(ProjectItem item)
    {
        int count = 1;
        foreach (var child in item.Children)
        {
            count += CountItems(child);
        }
        return count;
    }

    // ── Selection ────────────────────────────────────────────────────

    /// <summary>Currently selected items, in tree (depth-first) order.</summary>
    public IReadOnlyList<ProjectItem> SelectedItems => _selectedItems;

    /// <summary>The most recently clicked / right-clicked item (context-menu target).</summary>
    public ProjectItem? PrimaryItem => _primaryItem;

    private void ClearSelection()
    {
        foreach (var item in _selectedItems)
        {
            item.IsSelected = false;
        }
        _selectedItems.Clear();
        _primaryItem = null;
        _anchorItem = null;
        UpdateStatusText();
    }

    private void SelectSingle(ProjectItem item)
    {
        foreach (var selected in _selectedItems)
        {
            if (!ReferenceEquals(selected, item))
            {
                selected.IsSelected = false;
            }
        }
        _selectedItems.Clear();
        _selectedItems.Add(item);
        item.IsSelected = true;
        _anchorItem = item;
        UpdateStatusText();
    }

    private void ToggleSelect(ProjectItem item)
    {
        if (_selectedItems.Remove(item))
        {
            item.IsSelected = false;
            if (ReferenceEquals(_primaryItem, item))
            {
                _primaryItem = null;
            }
        }
        else
        {
            _selectedItems.Add(item);
            item.IsSelected = true;
        }
        UpdateStatusText();
    }

    private void SelectRange(ProjectItem anchor, ProjectItem clicked)
    {
        var visible = FlattenVisibleItems();
        int anchorIndex = visible.IndexOf(anchor);
        int clickedIndex = visible.IndexOf(clicked);
        if (anchorIndex < 0 || clickedIndex < 0)
        {
            SelectSingle(clicked);
            return;
        }

        bool additive = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        if (!additive)
        {
            foreach (var item in _selectedItems)
            {
                item.IsSelected = false;
            }
            _selectedItems.Clear();
        }

        int start = Math.Min(anchorIndex, clickedIndex);
        int end = Math.Max(anchorIndex, clickedIndex);
        for (int i = start; i <= end; i++)
        {
            var item = visible[i];
            if (!_selectedItems.Contains(item))
            {
                _selectedItems.Add(item);
                item.IsSelected = true;
            }
        }

        _primaryItem = clicked;
        _anchorItem = anchor;
        UpdateStatusText();
    }

    private void SelectAllVisible()
    {
        foreach (var item in _selectedItems)
        {
            item.IsSelected = false;
        }
        _selectedItems.Clear();

        var visible = FlattenVisibleItems();
        foreach (var item in visible)
        {
            _selectedItems.Add(item);
            item.IsSelected = true;
        }
        UpdateStatusText();
    }

    private void SelectAndFocus(ProjectItem item)
    {
        SelectSingle(item);
        _primaryItem = item;
        var container = FindContainer(ExplorerTree, item);
        container?.BringIntoView();
        container?.Focus();
    }

    private void MoveSelectionByOffset(int offset)
    {
        var visible = FlattenVisibleItems();
        if (visible.Count == 0)
        {
            return;
        }

        var current = _primaryItem ?? _anchorItem;
        int index = current is not null ? visible.IndexOf(current) : -1;
        int target = index < 0 ? 0 : Math.Clamp(index + offset, 0, visible.Count - 1);
        SelectAndFocus(visible[target]);
    }

    private void ExpandOrDescend()
    {
        if (_primaryItem is not { } item)
        {
            return;
        }

        if (item.Children.Count > 0 && !item.IsExpanded)
        {
            item.IsExpanded = true;
        }
        else if (item.Children.Count > 0)
        {
            var firstVisible = item.Children.FirstOrDefault(c => c.IsVisible);
            if (firstVisible is not null)
            {
                SelectAndFocus(firstVisible);
            }
        }
    }

    private void CollapseOrAscend()
    {
        if (_primaryItem is not { } item)
        {
            return;
        }

        if (item.IsExpanded)
        {
            item.IsExpanded = false;
            return;
        }

        var parent = FindParentItem(item);
        if (parent is not null)
        {
            SelectAndFocus(parent);
        }
    }

    private ProjectItem? FindParentItem(ProjectItem item)
    {
        foreach (var root in GetRootItems())
        {
            var parent = FindParentItem(root, item);
            if (parent is not null)
            {
                return parent;
            }
        }
        return null;
    }

    private static ProjectItem? FindParentItem(ProjectItem current, ProjectItem target)
    {
        if (current.Children.Contains(target))
        {
            return current;
        }

        foreach (var child in current.Children)
        {
            var parent = FindParentItem(child, target);
            if (parent is not null)
            {
                return parent;
            }
        }
        return null;
    }

    private static TreeViewItem? FindContainer(ItemsControl parent, ProjectItem item)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem direct)
        {
            return direct;
        }

        foreach (var childItem in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(childItem) is TreeViewItem childContainer)
            {
                var found = FindContainer(childContainer, item);
                if (found is not null)
                {
                    return found;
                }
            }
        }
        return null;
    }

    // ── Mouse interaction ────────────────────────────────────────────

    private void ExplorerTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _isDragging = false;

        // Never intercept scrollbar interaction.
        if (IsInsideScrollBar(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (FindItemContainer(e.OriginalSource as DependencyObject) is not TreeViewItem container ||
            container.DataContext is not ProjectItem item)
        {
            // Click on empty space clears the selection but still lets the
            // tree take keyboard focus.
            if (_selectedItems.Count > 0)
            {
                ClearSelection();
            }
            return;
        }

        // Clicking the expander only toggles the node; selection is preserved.
        if (IsExpanderClick(e.OriginalSource as DependencyObject))
        {
            item.IsExpanded = !item.IsExpanded;
            container.Focus();
            e.Handled = true;
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (ctrl)
        {
            ToggleSelect(item);
        }
        else if (shift)
        {
            SelectRange(_anchorItem ?? item, item);
        }
        else
        {
            SelectSingle(item);
        }

        _primaryItem = item;

        if (IsDoubleClick(item, e))
        {
            RaiseAction(ExplorerAction.Open, [item]);
        }

        container.Focus();
        e.Handled = true;
    }

    private bool IsDoubleClick(ProjectItem item, MouseButtonEventArgs e)
    {
        var now = DateTime.Now;
        var position = e.GetPosition(ExplorerTree);
        bool isDouble = ReferenceEquals(_lastClickItem, item)
            && now - _lastClickTime <= DoubleClickTime
            && Math.Abs(position.X - _lastClickPosition.X) <= DoubleClickTolerance
            && Math.Abs(position.Y - _lastClickPosition.Y) <= DoubleClickTolerance;

        _lastClickItem = item;
        _lastClickTime = now;
        _lastClickPosition = position;
        return isDouble;
    }

    private void ExplorerTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindItemContainer(e.OriginalSource as DependencyObject) is TreeViewItem container &&
            container.DataContext is ProjectItem item)
        {
            if (!_selectedItems.Contains(item))
            {
                SelectSingle(item);
            }
            _primaryItem = item;
        }
        else if (_selectedItems.Count > 0)
        {
            ClearSelection();
        }

        // Open the context menu manually so the default TreeViewItem
        // selection handling cannot collapse the multi-selection.
        e.Handled = true;
        if (ExplorerTree.ContextMenu is { } menu)
        {
            menu.PlacementTarget = ExplorerTree;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        }
    }

    private void ExplorerTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _isDragging)
        {
            return;
        }

        if (IsInsideScrollBar(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var currentPosition = e.GetPosition(null);
        if (Math.Abs(currentPosition.X - _dragStartPoint.X) <= SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPosition.Y - _dragStartPoint.Y) <= SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (FindItemContainer(e.OriginalSource as DependencyObject)?.DataContext is not ProjectItem item)
        {
            return;
        }

        // Dragging a non-selected item selects just it first.
        if (!_selectedItems.Contains(item))
        {
            SelectSingle(item);
        }

        var draggable = _selectedItems.Where(IsDraggable).ToList();
        if (draggable.Count == 0)
        {
            return;
        }

        _isDragging = true;
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, draggable.Select(i => i.FullPath).ToArray());
        DragDrop.DoDragDrop(ExplorerTree, data, DragDropEffects.Copy);
    }

    internal static bool IsDraggable(ProjectItem item) =>
        item.ItemType is ProjectItemType.File
            or ProjectItemType.Folder
            or ProjectItemType.Project
            or ProjectItemType.Solution;

    private static TreeViewItem? FindItemContainer(DependencyObject? source)
    {
        DependencyObject? current = source;
        while (current is not null && current is not TreeViewItem)
        {
            current = VisualTreeHelper.GetParent(current);
        }
        return current as TreeViewItem;
    }

    private static bool IsExpanderClick(DependencyObject? source)
    {
        DependencyObject? current = source;
        while (current is not null && current is not TreeViewItem)
        {
            if (current is ToggleButton)
            {
                return true;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private static bool IsInsideScrollBar(DependencyObject? source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is ScrollBar)
            {
                return true;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    // ── Keyboard interaction ─────────────────────────────────────────

    private void ExplorerTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var visible = FlattenVisibleItems();
        if (visible.Count == 0)
        {
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        switch (e.Key)
        {
            case Key.Delete when _selectedItems.Count > 0:
                RaiseAction(ExplorerAction.Delete, _selectedItems.ToList());
                e.Handled = true;
                break;

            case Key.F2 when _primaryItem is not null:
                RaiseAction(ExplorerAction.Rename, [_primaryItem]);
                e.Handled = true;
                break;

            case Key.Enter when _primaryItem is not null:
                RaiseAction(ExplorerAction.Open, [_primaryItem]);
                e.Handled = true;
                break;

            case Key.A when ctrl:
                SelectAllVisible();
                e.Handled = true;
                break;

            case Key.C when ctrl:
                CopyPaths();
                e.Handled = true;
                break;

            case Key.Down:
                MoveSelectionByOffset(1);
                e.Handled = true;
                break;

            case Key.Up:
                MoveSelectionByOffset(-1);
                e.Handled = true;
                break;

            case Key.PageDown:
                MoveSelectionByOffset(20);
                e.Handled = true;
                break;

            case Key.PageUp:
                MoveSelectionByOffset(-20);
                e.Handled = true;
                break;

            case Key.Home:
                SelectAndFocus(visible[0]);
                e.Handled = true;
                break;

            case Key.End:
                SelectAndFocus(visible[^1]);
                e.Handled = true;
                break;

            case Key.Right:
                ExpandOrDescend();
                e.Handled = true;
                break;

            case Key.Left:
                CollapseOrAscend();
                e.Handled = true;
                break;
        }
    }

    // ── Filtering ────────────────────────────────────────────────────

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterPlaceholder.Visibility = string.IsNullOrEmpty(FilterBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyFilter();
        UpdateStatusText();
    }

    private void ApplyFilter()
    {
        string filter = FilterBox.Text.Trim();
        foreach (var root in GetRootItems())
        {
            ApplyFilterToItem(root, filter);
        }

        PruneHiddenSelection();
    }

    /// <summary>Drops items from the selection that are no longer visible after filtering.</summary>
    private void PruneHiddenSelection()
    {
        var visible = FlattenVisibleItems().ToHashSet();
        bool changed = false;
        foreach (var item in _selectedItems.Where(i => !visible.Contains(i)).ToList())
        {
            _selectedItems.Remove(item);
            item.IsSelected = false;
            changed = true;
        }

        if (changed)
        {
            UpdateStatusText();
        }
    }

    /// <summary>
    /// Marks <paramref name="item"/> (and its subtree) visible when it matches the
    /// filter or contains a matching descendant. Expands ancestors of matches so
    /// results are revealed. A folder that matches the filter reveals its whole
    /// subtree (like VS Code). Returns whether the item ended up visible.
    /// </summary>
    internal static bool ApplyFilterToItem(ProjectItem item, string filter)
    {
        bool selfMatches = string.IsNullOrEmpty(filter)
            || item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        bool childMatches = false;
        foreach (var child in item.Children)
        {
            childMatches |= ApplyFilterToItem(child, filter);
        }

        bool visible = selfMatches || childMatches;
        item.IsVisible = visible;

        if (selfMatches && !string.IsNullOrEmpty(filter))
        {
            // A matching folder reveals its entire subtree.
            RevealSubtree(item);
        }
        else if (childMatches && !selfMatches)
        {
            // Reveal matching descendants by expanding their ancestors.
            item.IsExpanded = true;
        }

        return visible;
    }

    private static void RevealSubtree(ProjectItem item)
    {
        item.IsExpanded = true;
        foreach (var child in item.Children)
        {
            child.IsVisible = true;
            RevealSubtree(child);
        }
    }

    // ── Status bar ───────────────────────────────────────────────────

    private void UpdateStatusText()
    {
        int total = TotalItemCount();
        int visibleCount = FlattenVisibleItems().Count;
        int selectedCount = _selectedItems.Count;

        if (total == 0)
        {
            StatusText.Text = "No items";
            return;
        }

        string text = string.IsNullOrEmpty(FilterBox.Text)
            ? $"{total} items"
            : $"{visibleCount} of {total} items";

        if (selectedCount > 0)
        {
            text += $" · {selectedCount} selected";
        }

        StatusText.Text = text;
    }

    // ── Toolbar ──────────────────────────────────────────────────────

    private void Refresh_Click(object sender, RoutedEventArgs e) => RaiseAction(ExplorerAction.Refresh, []);

    private void CollapseAll_Click(object sender, RoutedEventArgs e) => CollapseAll();

    private void ExpandAll_Click(object sender, RoutedEventArgs e) => ExpandAll();

    private void NewFolder_Click(object sender, RoutedEventArgs e) =>
        RaiseAction(ExplorerAction.NewFolder, GetTargetItems());

    private void NewBlankFile_Click(object sender, RoutedEventArgs e) =>
        RaiseAction(ExplorerAction.NewBlankFile, GetTargetItems());

    private IReadOnlyList<ProjectItem> GetTargetItems() =>
        _primaryItem is { } primary ? [primary] : [];

    private void CollapseAll()
    {
        static void Collapse(ProjectItem item)
        {
            item.IsExpanded = false;
            foreach (var child in item.Children)
            {
                Collapse(child);
            }
        }

        foreach (var root in GetRootItems())
        {
            Collapse(root);
        }
        UpdateStatusText();
    }

    private void ExpandAll()
    {
        static void Expand(ProjectItem item)
        {
            if (item.Children.Count > 0)
            {
                item.IsExpanded = true;
            }
            foreach (var child in item.Children)
            {
                Expand(child);
            }
        }

        foreach (var root in GetRootItems())
        {
            Expand(root);
        }
        UpdateStatusText();
    }

    // ── Templates submenu (toolbar + context menu share this) ────────

    private void NewFileTemplates_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menu)
        {
            PopulateTemplatesMenu(menu);
        }
    }

    private void PopulateTemplatesMenu(MenuItem menu)
    {
        menu.Items.Clear();

        var args = new ExplorerActionEventArgs(ExplorerAction.RequestTemplates, [])
        {
            RoutedEvent = ExplorerActionRequestedEvent,
            Source = this
        };
        RaiseEvent(args);

        IReadOnlyList<FileTemplate> templates = args.Templates ?? [];
        if (templates.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(No templates)", IsEnabled = false });
            return;
        }

        foreach (var template in templates)
        {
            var item = new MenuItem { Header = template.Name, Tag = template.Name };
            item.Click += (_, _) => RaiseAction(
                ExplorerAction.NewFileFromTemplate,
                GetTargetItems(),
                template.Name);
            menu.Items.Add(item);
        }
    }

    // ── Context menu ─────────────────────────────────────────────────

    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        bool hasSelection = _selectedItems.Count > 0;
        ContextMenuOpen.IsEnabled = hasSelection;
        ContextMenuRename.IsEnabled = _primaryItem is not null;
        ContextMenuDelete.IsEnabled = hasSelection;
        ContextMenuCopyPath.IsEnabled = hasSelection;
        ContextMenuReveal.IsEnabled = _primaryItem is not null;
    }

    private void ContextMenu_Open(object sender, RoutedEventArgs e) =>
        RaiseAction(ExplorerAction.Open, _selectedItems.ToList());

    private void ContextMenu_Rename(object sender, RoutedEventArgs e)
    {
        if (_primaryItem is not null)
        {
            RaiseAction(ExplorerAction.Rename, [_primaryItem]);
        }
    }

    private void ContextMenu_Delete(object sender, RoutedEventArgs e) =>
        RaiseAction(ExplorerAction.Delete, _selectedItems.ToList());

    private void ContextMenu_NewFolder(object sender, RoutedEventArgs e) =>
        RaiseAction(ExplorerAction.NewFolder, GetTargetItems());

    private void ContextMenu_NewBlankFile(object sender, RoutedEventArgs e) =>
        RaiseAction(ExplorerAction.NewBlankFile, GetTargetItems());

    private void ContextMenu_CopyPath(object sender, RoutedEventArgs e) => CopyPaths();

    private void ContextMenu_RevealInExplorer(object sender, RoutedEventArgs e) => RevealInFileExplorer();

    private void ContextMenu_SelectAll(object sender, RoutedEventArgs e) => SelectAllVisible();

    private void CopyPaths()
    {
        var paths = _selectedItems.Select(i => i.FullPath).ToList();
        if (paths.Count > 0)
        {
            Clipboard.SetText(string.Join(Environment.NewLine, paths));
        }
    }

    private void RevealInFileExplorer()
    {
        if (_primaryItem is not { } item)
        {
            return;
        }

        var path = item.ItemType switch
        {
            ProjectItemType.Project or ProjectItemType.Solution => Path.GetDirectoryName(item.FullPath),
            _ when item.IsDirectory => item.FullPath,
            _ => Path.GetDirectoryName(item.FullPath)
        };

        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
    }

    // ── Host requests ────────────────────────────────────────────────

    private void RaiseAction(ExplorerAction action, IReadOnlyList<ProjectItem> items, string? templateName = null)
    {
        var args = new ExplorerActionEventArgs(action, items, templateName)
        {
            RoutedEvent = ExplorerActionRequestedEvent,
            Source = this
        };
        RaiseEvent(args);
    }
}
