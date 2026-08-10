using KaneCode.Services.Ai.Tools;

namespace KaneCode.Tests.Services.Ai.Tools;

public class ToolResultDetailsTests
{
    // ── CountLines ────────────────────────────────────────────────

    [Theory]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("a\n", 1)]
    [InlineData("a\nb", 2)]
    [InlineData("a\nb\n", 2)]
    [InlineData("a\n\nb", 3)]
    public void CountLines_CountsLinesCorrectly(string text, int expected)
    {
        Assert.Equal(expected, ToolResultDetails.CountLines(text));
    }

    // ── FormatByteSize ────────────────────────────────────────────

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(2048, "2 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    public void FormatByteSize_FormatsCompactStrings(long bytes, string expected)
    {
        Assert.Equal(expected, ToolResultDetails.FormatByteSize(bytes));
    }

    // ── GetDisplayPath ────────────────────────────────────────────

    [Fact]
    public void GetDisplayPath_ReturnsRelativePathWhenInsideRoot()
    {
        string resolved = System.IO.Path.Combine(@"C:\proj", "src", "File.cs");

        string display = ToolResultDetails.GetDisplayPath(resolved, @"C:\proj");

        Assert.Equal(System.IO.Path.Combine("src", "File.cs"), display);
    }

    [Fact]
    public void GetDisplayPath_ReturnsFullPathWhenOutsideRoot()
    {
        string resolved = @"D:\other\File.cs";

        string display = ToolResultDetails.GetDisplayPath(resolved, @"C:\proj");

        Assert.Equal(resolved, display);
    }

    [Fact]
    public void GetDisplayPath_FallsBackToFullPathWhenNoRoot()
    {
        string resolved = @"C:\proj\src\File.cs";

        string display = ToolResultDetails.GetDisplayPath(resolved, null);

        Assert.Equal(resolved, display);
    }

    // ── BuildLineDiff ─────────────────────────────────────────────

    [Fact]
    public void BuildLineDiff_ReturnsEmptyWhenTextsAreEqual()
    {
        Assert.Equal(string.Empty, ToolResultDetails.BuildLineDiff("same\n", "same\n"));
    }

    [Fact]
    public void BuildLineDiff_ShowsRemovedAndAddedLines()
    {
        string diff = ToolResultDetails.BuildLineDiff("old one\nold two", "new one\nnew two");

        Assert.Contains("- old one", diff, StringComparison.Ordinal);
        Assert.Contains("- old two", diff, StringComparison.Ordinal);
        Assert.Contains("+ new one", diff, StringComparison.Ordinal);
        Assert.Contains("+ new two", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildLineDiff_OmitsUnchangedContextLines()
    {
        string diff = ToolResultDetails.BuildLineDiff("keep\nold", "keep\nnew");

        Assert.DoesNotContain("- keep", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("+ keep", diff, StringComparison.Ordinal);
        Assert.Contains("- old", diff, StringComparison.Ordinal);
        Assert.Contains("+ new", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildLineDiff_CapsOutputAtMaxDiffLines()
    {
        string oldText = string.Join('\n', Enumerable.Range(0, 100).Select(i => $"old {i}"));
        string newText = string.Join('\n', Enumerable.Range(0, 100).Select(i => $"new {i}"));

        string diff = ToolResultDetails.BuildLineDiff(oldText, newText);

        int changedLines = diff.Split('\n').Count(line =>
            line.StartsWith("- ", StringComparison.Ordinal) ||
            line.StartsWith("+ ", StringComparison.Ordinal));
        Assert.Equal(ToolResultDetails.MaxDiffLines, changedLines);
        Assert.Contains("more changed line(s)", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildLineDiff_FallsBackToSummaryForVeryLargeInputs()
    {
        string oldText = string.Join('\n', Enumerable.Range(0, 1000).Select(i => $"old {i}"));
        string newText = string.Join('\n', Enumerable.Range(0, 1000).Select(i => $"new {i}"));

        string diff = ToolResultDetails.BuildLineDiff(oldText, newText);

        Assert.Contains("1000 removed line(s)", diff, StringComparison.Ordinal);
        Assert.Contains("1000 added line(s)", diff, StringComparison.Ordinal);
    }

    // ── BuildContentPreview ───────────────────────────────────────

    [Fact]
    public void BuildContentPreview_ReturnsNullForEmptyContent()
    {
        Assert.Null(ToolResultDetails.BuildContentPreview(string.Empty));
    }

    [Fact]
    public void BuildContentPreview_LimitsLinesAndNotesTruncation()
    {
        string content = string.Join('\n', Enumerable.Range(0, 20).Select(i => $"line {i}"));

        string? preview = ToolResultDetails.BuildContentPreview(content, maxLines: 5);

        Assert.NotNull(preview);
        Assert.Contains("line 0", preview, StringComparison.Ordinal);
        Assert.Contains("line 4", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("line 5", preview, StringComparison.Ordinal);
        Assert.Contains("15 more line(s)", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildContentPreview_IncludesLineNumbers()
    {
        string? preview = ToolResultDetails.BuildContentPreview("hello\nworld", maxLines: 2);

        Assert.NotNull(preview);
        Assert.Contains("1 | hello", preview, StringComparison.Ordinal);
        Assert.Contains("2 | world", preview, StringComparison.Ordinal);
    }
}
