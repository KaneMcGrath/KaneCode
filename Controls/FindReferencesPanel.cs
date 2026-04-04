using KaneCode.Models;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KaneCode.Controls;

/// <summary>
/// A panel that displays grouped Roslyn navigation results and an inline peek preview.
/// </summary>
public partial class FindReferencesPanel : UserControl
{
    private readonly ObservableCollection<ReferenceTreeNode> _treeNodes = [];

    private TreeView? ReferencesTreeControl => FindName("ReferencesTree") as TreeView;

    public FindReferencesPanel()
    {
        InitializeComponent();
        TreeView? referencesTree = ReferencesTreeControl;
        if (referencesTree is not null)
        {
            referencesTree.ItemsSource = _treeNodes;
        }
    }

    /// <summary>
    /// The references collection displayed in the navigation tree.
    /// </summary>
    public static readonly DependencyProperty ReferencesProperty =
        DependencyProperty.Register(
            nameof(References),
            typeof(ObservableCollection<ReferenceItem>),
            typeof(FindReferencesPanel),
            new PropertyMetadata(null, OnReferencesChanged));

    public ObservableCollection<ReferenceItem>? References
    {
        get => (ObservableCollection<ReferenceItem>?)GetValue(ReferencesProperty);
        set => SetValue(ReferencesProperty, value);
    }

    /// <summary>
    /// Status text shown in the header (e.g., symbol name and match count).
    /// </summary>
    public static readonly DependencyProperty PanelStatusTextProperty =
        DependencyProperty.Register(
            nameof(PanelStatusText),
            typeof(string),
            typeof(FindReferencesPanel),
            new PropertyMetadata(string.Empty, OnPanelStatusTextChanged));

    public string PanelStatusText
    {
        get => (string)GetValue(PanelStatusTextProperty);
        set => SetValue(PanelStatusTextProperty, value);
    }

    public static readonly DependencyProperty PeekHeaderTextProperty =
        DependencyProperty.Register(
            nameof(PeekHeaderText),
            typeof(string),
            typeof(FindReferencesPanel),
            new PropertyMetadata("Select a result to preview"));

    public string PeekHeaderText
    {
        get => (string)GetValue(PeekHeaderTextProperty);
        set => SetValue(PeekHeaderTextProperty, value);
    }

    public static readonly DependencyProperty PeekContentTextProperty =
        DependencyProperty.Register(
            nameof(PeekContentText),
            typeof(string),
            typeof(FindReferencesPanel),
            new PropertyMetadata(string.Empty));

    public string PeekContentText
    {
        get => (string)GetValue(PeekContentTextProperty);
        set => SetValue(PeekContentTextProperty, value);
    }

    /// <summary>
    /// Raised when the user double-clicks a navigation result to navigate to source.
    /// </summary>
    public event EventHandler<ReferenceItem>? NavigateRequested;

    /// <summary>
    /// Raised when the selected navigation result changes so the host can update the peek preview.
    /// </summary>
    public event EventHandler<ReferenceItem?>? PreviewRequested;

    private static void OnReferencesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FindReferencesPanel panel)
        {
            panel.RebuildTree(e.NewValue as IEnumerable<ReferenceItem> ?? []);
        }
    }

    private static void OnPanelStatusTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FindReferencesPanel panel)
        {
            panel.StatusText.Text = e.NewValue as string ?? string.Empty;
        }
    }

    private void RebuildTree(IEnumerable<ReferenceItem> references)
    {
        _treeNodes.Clear();

        IEnumerable<IGrouping<string, ReferenceItem>> projectGroups = references
            .GroupBy(static item => string.IsNullOrWhiteSpace(item.ProjectName) ? "(Unknown Project)" : item.ProjectName)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase);

        foreach (IGrouping<string, ReferenceItem> projectGroup in projectGroups)
        {
            ReferenceTreeNode projectNode = new(projectGroup.Key, "📦", Brushes.SlateBlue, isExpanded: true);

            IEnumerable<IGrouping<string, ReferenceItem>> fileGroups = projectGroup
                .GroupBy(static item => item.FilePath)
                .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase);

            foreach (IGrouping<string, ReferenceItem> fileGroup in fileGroups)
            {
                ReferenceItem firstFileItem = fileGroup.First();
                ReferenceTreeNode fileNode = new(firstFileItem.FileName, "📄", Brushes.SteelBlue, isExpanded: true);

                IEnumerable<IGrouping<ReferenceKind, ReferenceItem>> kindGroups = fileGroup
                    .GroupBy(static item => item.Kind)
                    .OrderBy(static group => GetKindSortOrder(group.Key));

                foreach (IGrouping<ReferenceKind, ReferenceItem> kindGroup in kindGroups)
                {
                    ReferenceItem firstKindItem = kindGroup.First();
                    ReferenceTreeNode kindNode = new(
                        $"{firstKindItem.KindDisplayName} ({kindGroup.Count()})",
                        firstKindItem.KindIcon,
                        GetAccentBrush(firstKindItem.Kind),
                        isExpanded: true);

                    foreach (ReferenceItem item in kindGroup.OrderBy(static entry => entry.Line).ThenBy(static entry => entry.Column))
                    {
                        string leafText = $"{item.Line}:{item.Column} — {item.Preview}";
                        kindNode.Children.Add(new ReferenceTreeNode(leafText, item.KindIcon, GetAccentBrush(item.Kind), item));
                    }

                    fileNode.Children.Add(kindNode);
                }

                projectNode.Children.Add(fileNode);
            }

            _treeNodes.Add(projectNode);
        }

        PreviewRequested?.Invoke(this, GetSelectedReferenceItem());
    }

    private void ReferencesTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ReferenceItem? item = GetSelectedReferenceItem();
        if (item is not null)
        {
            NavigateRequested?.Invoke(this, item);
        }
    }

    private void ReferencesTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        PreviewRequested?.Invoke(this, GetSelectedReferenceItem());
    }

    private void ContextMenu_GoToReference(object sender, RoutedEventArgs e)
    {
        ReferenceItem? item = GetSelectedReferenceItem();
        if (item is not null)
        {
            NavigateRequested?.Invoke(this, item);
        }
    }

    private void ContextMenu_PeekDefinition(object sender, RoutedEventArgs e)
    {
        PreviewRequested?.Invoke(this, GetSelectedReferenceItem());
    }

    private void ContextMenu_CopyPath(object sender, RoutedEventArgs e)
    {
        ReferenceItem? item = GetSelectedReferenceItem();
        if (item is not null)
        {
            Clipboard.SetText(item.FilePath);
        }
    }

    private void ContextMenu_CopyLine(object sender, RoutedEventArgs e)
    {
        ReferenceItem? item = GetSelectedReferenceItem();
        if (item is not null)
        {
            Clipboard.SetText($"{item.FileName}({item.Line},{item.Column}): {item.Preview}");
        }
    }

    private ReferenceItem? GetSelectedReferenceItem()
    {
        TreeView? referencesTree = ReferencesTreeControl;
        if (referencesTree?.SelectedItem is ReferenceTreeNode node)
        {
            return node.Item;
        }

        return null;
    }

    private static int GetKindSortOrder(ReferenceKind kind)
    {
        return kind switch
        {
            ReferenceKind.Definition => 0,
            ReferenceKind.Reference => 1,
            ReferenceKind.Implementation => 2,
            _ => 3
        };
    }

    private static Brush GetAccentBrush(ReferenceKind kind)
    {
        return kind switch
        {
            ReferenceKind.Definition => Brushes.Goldenrod,
            ReferenceKind.Implementation => Brushes.MediumSeaGreen,
            _ => Brushes.DeepSkyBlue
        };
    }

    private sealed class ReferenceTreeNode
    {
        public ReferenceTreeNode(string text, string icon, Brush accentBrush, ReferenceItem? item = null, bool isExpanded = false)
        {
            Text = text;
            Icon = icon;
            AccentBrush = accentBrush;
            Item = item;
            IsExpanded = isExpanded;
        }

        public string Text { get; }

        public string Icon { get; }

        public Brush AccentBrush { get; }

        public ReferenceItem? Item { get; }

        public bool IsExpanded { get; }

        public ObservableCollection<ReferenceTreeNode> Children { get; } = [];
    }
}
