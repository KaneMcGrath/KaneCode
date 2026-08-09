using System.ComponentModel;
using KaneCode.Models;
using Xunit;

namespace KaneCode.Tests.Models;

public class ProjectItemTests
{
    [Theory]
    [InlineData(@"C:\repo\DogService.cs", "cs")]
    [InlineData(@"C:\repo\App.xaml", "xaml")]
    [InlineData(@"C:\repo\README.MD", "md")]
    [InlineData(@"C:\repo\noextension", "")]
    public void WhenFileExtensionIsComputedThenItIsLowerCasedWithoutDot(string path, string expected)
    {
        ProjectItem item = new(path, isDirectory: false);

        Assert.Equal(expected, item.FileExtension);
    }

    [Fact]
    public void WhenItemIsDirectoryThenFileExtensionIsEmpty()
    {
        ProjectItem folder = new(@"C:\repo\Services", isDirectory: true);

        Assert.Equal(string.Empty, folder.FileExtension);
    }

    [Fact]
    public void WhenIsVisibleChangesThenPropertyChangedIsRaised()
    {
        ProjectItem item = new(@"C:\repo\Dog.cs", isDirectory: false);
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.IsVisible = false;

        Assert.Contains(nameof(ProjectItem.IsVisible), changed);
        Assert.False(item.IsVisible);
    }

    [Fact]
    public void WhenIsVisibleSetToSameValueThenNoPropertyChangedIsRaised()
    {
        ProjectItem item = new(@"C:\repo\Dog.cs", isDirectory: false);
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.IsVisible = true;

        Assert.DoesNotContain(nameof(ProjectItem.IsVisible), changed);
    }

    [Fact]
    public void WhenIsSelectedSetToSameValueThenNoPropertyChangedIsRaised()
    {
        ProjectItem item = new(@"C:\repo\Dog.cs", isDirectory: false);
        item.IsSelected = true;

        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.IsSelected = true;

        Assert.Empty(changed);
    }
}
