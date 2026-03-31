using KaneCode.Models;

namespace KaneCode.Tests.Models;

public class SolutionEditResultTests
{
    [Fact]
    public void WhenAllCollectionsEmptyThenIsEmptyReturnsTrue()
    {
        SolutionEditResult result = new SolutionEditResult(
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            Array.Empty<string>());

        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void WhenChangedFilesNotEmptyThenIsEmptyReturnsFalse()
    {
        SolutionEditResult result = new SolutionEditResult(
            new Dictionary<string, string> { ["file.cs"] = "content" },
            new Dictionary<string, string>(),
            Array.Empty<string>());

        Assert.False(result.IsEmpty);
    }

    [Fact]
    public void WhenAddedFilesNotEmptyThenIsEmptyReturnsFalse()
    {
        SolutionEditResult result = new SolutionEditResult(
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["new.cs"] = "content" },
            Array.Empty<string>());

        Assert.False(result.IsEmpty);
    }

    [Fact]
    public void WhenRemovedFilesNotEmptyThenIsEmptyReturnsFalse()
    {
        SolutionEditResult result = new SolutionEditResult(
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            new[] { "deleted.cs" });

        Assert.False(result.IsEmpty);
    }
}
