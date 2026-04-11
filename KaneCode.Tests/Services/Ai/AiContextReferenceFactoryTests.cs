using KaneCode.Models;
using KaneCode.Services.Ai;
using System.IO;
using System.Linq;

namespace KaneCode.Tests.Services.Ai;

public sealed class AiContextReferenceFactoryTests : IDisposable
{
    private readonly string _tempDirectory;

    public AiContextReferenceFactoryTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"KaneCodeContextFactory_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void WhenDiscoveringClassesThenQualifiedNamesAreReturned()
    {
        string filePath = Path.Combine(_tempDirectory, "Sample.cs");
        File.WriteAllText(filePath, "namespace Demo.App; public class Outer { public class Inner { } }");
        List<ProjectItem> projectItems =
        [
            new ProjectItem(filePath, isDirectory: false)
        ];

        IReadOnlyList<AiContextClassSnapshot> classes = AiContextReferenceFactory.DiscoverClasses(projectItems);

        Assert.Equal(["Demo.App.Outer", "Demo.App.Outer.Inner"], classes.Select(item => item.DisplayName).ToArray());
    }

    [Fact]
    public void WhenCreatingExternalFolderReferenceThenToolAccessInstructionsAreIncluded()
    {
        string folderPath = Path.Combine(_tempDirectory, "External");
        Directory.CreateDirectory(folderPath);
        File.WriteAllText(Path.Combine(folderPath, "notes.txt"), "hello");

        AiChatReference reference = AiContextReferenceFactory.CreateExternalFolderReference(folderPath);
        string normalizedContext = reference.ToContextString().Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("For this request only, the agent may use read_file, list_files, and search_files", normalizedContext, StringComparison.Ordinal);
        Assert.Contains("notes.txt", normalizedContext, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenCreatingBuildOutputReferenceThenOnlyRecentLinesAreIncluded()
    {
        List<string> lines = Enumerable.Range(1, 260).Select(index => $"line {index}").ToList();
        AiBuildOutputSnapshot snapshot = new("Build succeeded", lines);

        AiChatReference reference = AiContextReferenceFactory.CreateBuildOutputReference(snapshot);
        string normalizedContent = reference.Content.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.DoesNotContain("\nline 10\n", normalizedContent, StringComparison.Ordinal);
        Assert.Contains("line 11", normalizedContent, StringComparison.Ordinal);
        Assert.Contains("line 260", normalizedContent, StringComparison.Ordinal);
    }
}
