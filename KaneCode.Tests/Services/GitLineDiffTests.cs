using KaneCode.Services;
using System.Collections.Generic;
using System.Linq;

namespace KaneCode.Tests.Services;

public class GitLineDiffTests
{
    [Fact]
    public void ComputeChanges_IdenticalText_ReturnsNoChanges()
    {
        const string text = "line one\nline two\nline three";

        IReadOnlyList<GitLineChange> changes = GitLineDiff.ComputeChanges(text, text);

        Assert.Empty(changes);
    }

    [Fact]
    public void ComputeChanges_InsertedLineInMiddle_MarksOnlyInsertedLine()
    {
        const string head = "alpha\nbeta\ngamma";
        const string current = "alpha\nbeta\nNEW\ngamma";

        IReadOnlyList<GitLineChange> changes = GitLineDiff.ComputeChanges(head, current);

        GitLineChange change = Assert.Single(changes);
        Assert.Equal(3, change.LineNumber);
        Assert.Equal(GitLineChangeType.Added, change.ChangeType);
    }

    [Fact]
    public void ComputeChanges_DeletedLineInMiddle_AnchorsMarkerWithoutFlaggingFollowingLines()
    {
        const string head = "alpha\nbeta\nREMOVE\ngamma";
        const string current = "alpha\nbeta\ngamma";

        IReadOnlyList<GitLineChange> changes = GitLineDiff.ComputeChanges(head, current);

        GitLineChange change = Assert.Single(changes);
        Assert.Equal(3, change.LineNumber);
        Assert.Equal(GitLineChangeType.Deleted, change.ChangeType);
    }

    [Fact]
    public void ComputeChanges_ModifiedLine_ReturnsModifiedMarker()
    {
        const string head = "alpha\nbeta\ngamma";
        const string current = "alpha\nBETA-CHANGED\ngamma";

        IReadOnlyList<GitLineChange> changes = GitLineDiff.ComputeChanges(head, current);

        GitLineChange change = Assert.Single(changes);
        Assert.Equal(2, change.LineNumber);
        Assert.Equal(GitLineChangeType.Modified, change.ChangeType);
    }

    [Fact]
    public void ComputeChanges_AddedLinesAtStart_ReturnsAddedMarkersAtStart()
    {
        const string head = "alpha\nbeta";
        const string current = "new1\nnew2\nalpha\nbeta";

        IReadOnlyList<GitLineChange> changes = GitLineDiff.ComputeChanges(head, current);

        Assert.Equal(2, changes.Count);
        Assert.All(changes, change =>
        {
            Assert.Equal(GitLineChangeType.Added, change.ChangeType);
            Assert.True(change.LineNumber is 1 or 2);
        });
    }

    [Fact]
    public void ComputeChanges_EntireFileDeleted_AnchorsDeletedMarker()
    {
        const string head = "alpha\nbeta\ngamma";
        const string current = "";

        IReadOnlyList<GitLineChange> changes = GitLineDiff.ComputeChanges(head, current);

        // All lines are deleted; the single marker is anchored at the first current line.
        GitLineChange change = Assert.Single(changes);
        Assert.Equal(GitLineChangeType.Deleted, change.ChangeType);
    }

    [Fact]
    public void ComputeChanges_CrLfOnlyDifference_ReturnsNoChanges()
    {
        const string head = "alpha\nbeta";
        const string current = "alpha\r\nbeta\r\n";

        IReadOnlyList<GitLineChange> changes = GitLineDiff.ComputeChanges(head, current);

        Assert.Empty(changes);
    }

    [Fact]
    public void ComputeChanges_TrailingNewlineDifference_ReturnsNoChanges()
    {
        const string head = "alpha\nbeta\n";
        const string current = "alpha\nbeta";

        IReadOnlyList<GitLineChange> changes = GitLineDiff.ComputeChanges(head, current);

        Assert.Empty(changes);
    }

    [Fact]
    public void ComputeSideChanges_Insertion_PutsAddedOnRightOnly()
    {
        const string leftText = "alpha\nbeta";
        const string rightText = "alpha\nNEW\nbeta";

        GitLineDiffResult result = GitLineDiff.ComputeSideChanges(leftText, rightText);

        Assert.Empty(result.LeftChanges);
        GitLineChange change = Assert.Single(result.RightChanges);
        Assert.Equal(2, change.LineNumber);
        Assert.Equal(GitLineChangeType.Added, change.ChangeType);
    }

    [Fact]
    public void ComputeSideChanges_Deletion_PutsDeletedOnLeftOnly()
    {
        const string leftText = "alpha\nREMOVE\nbeta";
        const string rightText = "alpha\nbeta";

        GitLineDiffResult result = GitLineDiff.ComputeSideChanges(leftText, rightText);

        Assert.Empty(result.RightChanges);
        GitLineChange change = Assert.Single(result.LeftChanges);
        Assert.Equal(2, change.LineNumber);
        Assert.Equal(GitLineChangeType.Deleted, change.ChangeType);
    }

    [Fact]
    public void ComputeSideChanges_ReplacedBlock_MarksBothSidesModified()
    {
        const string leftText = "alpha\nold1\nold2\nbeta";
        const string rightText = "alpha\nnew1\nnew2\nbeta";

        GitLineDiffResult result = GitLineDiff.ComputeSideChanges(leftText, rightText);

        Assert.Equal(2, result.LeftChanges.Count);
        Assert.Equal(2, result.RightChanges.Count);
        Assert.All(result.LeftChanges, change => Assert.Equal(GitLineChangeType.Modified, change.ChangeType));
        Assert.All(result.RightChanges, change => Assert.Equal(GitLineChangeType.Modified, change.ChangeType));
        Assert.Equal(new[] { 2, 3 }, result.LeftChanges.Select(c => c.LineNumber));
        Assert.Equal(new[] { 2, 3 }, result.RightChanges.Select(c => c.LineNumber));
    }

    [Fact]
    public void ComputeChanges_InsertionAfterModification_KeepsFollowingLinesClean()
    {
        const string head = "alpha\nbeta\ngamma\ndelta";
        const string current = "alpha\nBETA\ngamma\nINSERTED\ndelta";

        IReadOnlyList<GitLineChange> changes = GitLineDiff.ComputeChanges(head, current);

        // Line 2 modified, line 4 added; line 3 and 5 must not be flagged.
        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.LineNumber == 2 && c.ChangeType == GitLineChangeType.Modified);
        Assert.Contains(changes, c => c.LineNumber == 4 && c.ChangeType == GitLineChangeType.Added);
    }
}
