using ICSharpCode.AvalonEdit.Document;
using KaneCode.Models;

namespace KaneCode.Tests.Models;

public class OpenFileTabTests
{
    [Fact]
    public void WhenConstructedThenIsDirtyIsFalse()
    {
        OpenFileTab tab = new OpenFileTab(@"C:\test\File.cs", "initial content");

        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void WhenDocumentTextSetProgrammaticallyThenIsDirtyCanBeSetToTrue()
    {
        OpenFileTab tab = new OpenFileTab(@"C:\test\File.cs", "initial content");

        tab.Document.Text = "changed content";
        tab.IsDirty = true;

        Assert.True(tab.IsDirty);
        Assert.Equal("changed content", tab.Document.Text);
    }

    [Fact]
    public void WhenIsDirtySetThenDisplayNameShowsAsterisk()
    {
        OpenFileTab tab = new OpenFileTab(@"C:\test\File.cs", "content");

        tab.IsDirty = true;

        Assert.Equal("File.cs *", tab.DisplayName);
    }

    [Fact]
    public void WhenIsDirtyNotSetThenDisplayNameHasNoAsterisk()
    {
        OpenFileTab tab = new OpenFileTab(@"C:\test\File.cs", "content");

        Assert.Equal("File.cs", tab.DisplayName);
    }

    [Fact]
    public void WhenIsDirtyChangedThenPropertyChangedRaisedForIsDirtyAndDisplayName()
    {
        OpenFileTab tab = new OpenFileTab(@"C:\test\File.cs", "content");
        List<string> changedProperties = [];
        tab.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

        tab.IsDirty = true;

        Assert.Contains(nameof(OpenFileTab.IsDirty), changedProperties);
        Assert.Contains(nameof(OpenFileTab.DisplayName), changedProperties);
    }

    [Fact]
    public void WhenDocumentTextReplacedThenUndoRestoresOriginalContent()
    {
        OpenFileTab tab = new OpenFileTab(@"C:\test\File.cs", "original");

        tab.Document.Text = "replaced";

        Assert.Equal("replaced", tab.Document.Text);

        tab.Document.UndoStack.Undo();

        Assert.Equal("original", tab.Document.Text);
    }

    [Fact]
    public void WhenDocumentTextReplacedThenRedoReappliesNewContent()
    {
        OpenFileTab tab = new OpenFileTab(@"C:\test\File.cs", "original");

        tab.Document.Text = "replaced";
        tab.Document.UndoStack.Undo();
        tab.Document.UndoStack.Redo();

        Assert.Equal("replaced", tab.Document.Text);
    }

    [Fact]
    public void WhenMultipleTabsEditedProgrammaticallyThenEachTabHasIndependentUndoStack()
    {
        OpenFileTab tab1 = new OpenFileTab(@"C:\test\A.cs", "A-original");
        OpenFileTab tab2 = new OpenFileTab(@"C:\test\B.cs", "B-original");

        tab1.Document.Text = "A-changed";
        tab2.Document.Text = "B-changed";

        // Undo only tab1
        tab1.Document.UndoStack.Undo();

        Assert.Equal("A-original", tab1.Document.Text);
        Assert.Equal("B-changed", tab2.Document.Text);
    }

    [Fact]
    public void WhenConstructedWithNullFilePathThenThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new OpenFileTab(null!));
    }
}
