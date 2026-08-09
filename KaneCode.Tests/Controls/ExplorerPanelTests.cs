using KaneCode.Controls;
using KaneCode.Models;
using Xunit;

namespace KaneCode.Tests.Controls;

public class ExplorerPanelTests
{
    // ── Filtering ───────────────────────────────────────────────────

    [Fact]
    public void WhenFilterIsEmptyThenEveryNodeStaysVisible()
    {
        ProjectItem root = new(@"C:\repo\App.csproj", ProjectItemType.Project);
        root.Children.Add(new ProjectItem(@"C:\repo\Services", isDirectory: true));
        root.Children.Add(new ProjectItem(@"C:\repo\README.md", isDirectory: false));

        Assert.True(ExplorerPanel.ApplyFilterToItem(root, string.Empty));
        Assert.True(root.IsVisible);
        Assert.True(root.Children[0].IsVisible);
        Assert.True(root.Children[1].IsVisible);
    }

    [Fact]
    public void WhenNameMatchesFilterThenNodeIsVisible()
    {
        ProjectItem file = new(@"C:\repo\DogService.cs", isDirectory: false);

        Assert.True(ExplorerPanel.ApplyFilterToItem(file, "dogservice"));
        Assert.True(file.IsVisible);
    }

    [Fact]
    public void WhenLeafDoesNotMatchFilterThenItIsHidden()
    {
        ProjectItem file = new(@"C:\repo\DogService.cs", isDirectory: false);

        Assert.False(ExplorerPanel.ApplyFilterToItem(file, "cat"));
        Assert.False(file.IsVisible);
    }

    [Fact]
    public void WhenDescendantMatchesThenAncestorStaysVisibleAndExpands()
    {
        ProjectItem folder = new(@"C:\repo\Services", isDirectory: true);
        folder.Children.Add(new ProjectItem(@"C:\repo\Services\CatService.cs", isDirectory: false));

        Assert.True(ExplorerPanel.ApplyFilterToItem(folder, "cat"));
        Assert.True(folder.IsVisible);
        Assert.True(folder.IsExpanded, "Ancestors of a match should be auto-expanded to reveal it.");
        Assert.True(folder.Children[0].IsVisible);
    }

    [Fact]
    public void WhenFolderMatchesFilterThenItsDescendantsRemainVisible()
    {
        ProjectItem folder = new(@"C:\repo\CatFolder", isDirectory: true);
        folder.Children.Add(new ProjectItem(@"C:\repo\CatFolder\notes.txt", isDirectory: false));

        Assert.True(ExplorerPanel.ApplyFilterToItem(folder, "cat"));
        Assert.True(folder.IsVisible);
        Assert.True(folder.IsExpanded, "A matching folder should expand to reveal its subtree.");
        Assert.True(folder.Children[0].IsVisible, "Descendants of a matching folder remain visible.");
    }

    [Fact]
    public void WhenFilterClearedThenAllNodesBecomeVisibleAgain()
    {
        ProjectItem root = new(@"C:\repo\App.csproj", ProjectItemType.Project) { IsExpanded = true };
        root.Children.Add(new ProjectItem(@"C:\repo\DogService.cs", isDirectory: false));

        ExplorerPanel.ApplyFilterToItem(root, "dogservice");
        Assert.True(root.Children[0].IsVisible);

        ExplorerPanel.ApplyFilterToItem(root, string.Empty);
        Assert.True(root.IsVisible);
        Assert.True(root.Children[0].IsVisible);
    }

    // ── Visible flattening ──────────────────────────────────────────

    [Fact]
    public void WhenFlatteningVisibleItemsThenExpandedChildrenAreIncludedInDepthFirstOrder()
    {
        ProjectItem root = new(@"C:\repo", isDirectory: true) { IsExpanded = true };
        ProjectItem folder = new(@"C:\repo\Services", isDirectory: true) { IsExpanded = true };
        ProjectItem inner = new(@"C:\repo\Services\Dog.cs", isDirectory: false);
        ProjectItem topFile = new(@"C:\repo\README.md", isDirectory: false);
        ProjectItem collapsedFolder = new(@"C:\repo\Docs", isDirectory: true);
        ProjectItem hiddenInCollapsed = new(@"C:\repo\Docs\Guide.md", isDirectory: false);
        collapsedFolder.Children.Add(hiddenInCollapsed);

        folder.Children.Add(inner);
        root.Children.Add(folder);
        root.Children.Add(topFile);
        root.Children.Add(collapsedFolder);

        IReadOnlyList<ProjectItem> flattened = ExplorerPanel.FlattenVisibleItems([root]);

        Assert.Equal([root, folder, inner, topFile, collapsedFolder], flattened);
    }

    [Fact]
    public void WhenFlatteningVisibleItemsThenInvisibleNodesAreExcluded()
    {
        ProjectItem root = new(@"C:\repo", isDirectory: true) { IsExpanded = true };
        ProjectItem hidden = new(@"C:\repo\obj", isDirectory: false) { IsVisible = false };
        root.Children.Add(hidden);

        IReadOnlyList<ProjectItem> flattened = ExplorerPanel.FlattenVisibleItems([root]);

        Assert.DoesNotContain(hidden, flattened);
    }

    // ── Drag eligibility ────────────────────────────────────────────

    [Theory]
    [InlineData(ProjectItemType.File)]
    [InlineData(ProjectItemType.Folder)]
    [InlineData(ProjectItemType.Project)]
    [InlineData(ProjectItemType.Solution)]
    public void WhenItemIsRealFileSystemNodeThenItIsDraggable(ProjectItemType itemType)
    {
        Assert.True(ExplorerPanel.IsDraggable(new ProjectItem(@"C:\repo\thing", itemType)));
    }

    [Theory]
    [InlineData(ProjectItemType.Dependencies)]
    [InlineData(ProjectItemType.Framework)]
    [InlineData(ProjectItemType.Package)]
    public void WhenItemIsVirtualNodeThenItIsNotDraggable(ProjectItemType itemType)
    {
        Assert.False(ExplorerPanel.IsDraggable(new ProjectItem(@"C:\repo\.dependencies\x", itemType)));
    }

    // ── Action args ─────────────────────────────────────────────────

    [Fact]
    public void WhenBuildingActionArgsThenValuesAreExposed()
    {
        ProjectItem item = new(@"C:\repo\Dog.cs", isDirectory: false);
        ExplorerActionEventArgs args = new(ExplorerAction.Delete, [item], templateName: "Class");

        Assert.Equal(ExplorerAction.Delete, args.Action);
        Assert.Same(item, Assert.Single(args.Items));
        Assert.Equal("Class", args.TemplateName);
        Assert.Null(args.Templates);
    }
}
