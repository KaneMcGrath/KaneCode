using KaneCode.Models;
using KaneCode.Services.Ai;

namespace KaneCode.Tests.Services.Ai;

public class AiProjectContextBuilderTests
{
    [Fact]
    public void WhenRootItemsIsEmptyThenReturnsEmptyString()
    {
        string result = AiProjectContextBuilder.Build([]);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void WhenRootItemsIsNullThenThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => AiProjectContextBuilder.Build(null!));
    }

    [Fact]
    public void WhenSingleFileProvidedThenOutputContainsFileName()
    {
        List<ProjectItem> items =
        [
            new ProjectItem("TestFile.cs", ProjectItemType.File)
        ];

        string result = AiProjectContextBuilder.Build(items);

        Assert.Contains("TestFile.cs", result);
        Assert.Contains("File tree:", result);
    }

    [Fact]
    public void WhenFolderWithChildrenProvidedThenOutputContainsBoth()
    {
        ProjectItem folder = new ProjectItem("Services", ProjectItemType.Folder);
        folder.Children.Add(new ProjectItem("MyService.cs", ProjectItemType.File));
        List<ProjectItem> items = [folder];

        string result = AiProjectContextBuilder.Build(items);

        Assert.Contains("Services", result);
        Assert.Contains("MyService.cs", result);
    }

    [Fact]
    public void WhenBuildCalledThenOutputContainsProjectContextHeader()
    {
        List<ProjectItem> items =
        [
            new ProjectItem("App.cs", ProjectItemType.File)
        ];

        string result = AiProjectContextBuilder.Build(items);

        Assert.Contains("Project-wide context:", result);
    }
}
