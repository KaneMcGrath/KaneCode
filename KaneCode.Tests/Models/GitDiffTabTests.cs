using KaneCode.Models;

namespace KaneCode.Tests.Models;

public class GitDiffTabTests
{
    [Fact]
    public void WhenCreatedThenDisplayNameUsesFileName()
    {
        var tab = new GitDiffTab("src/Program.cs", "old", "new");

        Assert.Equal("Program.cs", tab.FileName);
        Assert.Equal("Diff: Program.cs", tab.DisplayName);
        Assert.Equal("src/Program.cs", tab.RelativePath);
        Assert.Equal("old", tab.OriginalText);
        Assert.Equal("new", tab.ModifiedText);
    }

    [Fact]
    public void WhenUpdatedThenDiffContentIsRefreshed()
    {
        var tab = new GitDiffTab("src/Program.cs", "old", "new");

        tab.Update("older", "newer");

        Assert.Equal("older", tab.OriginalText);
        Assert.Equal("newer", tab.ModifiedText);
        Assert.Equal("Diff: Program.cs", tab.DisplayName);
    }

    [Fact]
    public void WhenPathHasNoDirectoryThenFileNameIsThePath()
    {
        var tab = new GitDiffTab("Program.cs", "old", "new");

        Assert.Equal("Program.cs", tab.FileName);
    }
}
