using System.IO;
using KaneCode.Services.Ai.Tools;

namespace KaneCode.Tests.Services.Ai.Tools;

public sealed class AgentToolPathResolverTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly string _projectFilePath;

    public AgentToolPathResolverTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), $"KaneCodeProject_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectRoot);
        _projectFilePath = Path.Combine(_projectRoot, "TestProject.csproj");
        File.WriteAllText(_projectFilePath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); }
        catch { }
    }

    [Fact]
    public void WhenProviderReturnsProjectFileThenReturnsProjectDirectory()
    {
        string root = AgentToolPathResolver.GetProjectRootDirectory(() => _projectFilePath);

        Assert.Equal(Path.GetFullPath(_projectRoot), root, ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void WhenRelativePathIsInsideProjectThenResolvesAgainstProjectRoot()
    {
        string resolvedPath = AgentToolPathResolver.ResolvePath(() => _projectRoot, Path.Combine("src", "Program.cs"));
        string expectedPath = Path.GetFullPath(Path.Combine(_projectRoot, "src", "Program.cs"));

        Assert.Equal(expectedPath, resolvedPath, ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void WhenAbsolutePathIsOutsideProjectThenThrowsInvalidOperationException()
    {
        string outsideDirectory = Path.Combine(Path.GetTempPath(), $"KaneCodeOutside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDirectory);

        try
        {
            string outsidePath = Path.Combine(outsideDirectory, "outside.cs");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => AgentToolPathResolver.ResolvePath(() => _projectRoot, outsidePath));

            Assert.Contains("inside the loaded project", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(outsideDirectory, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void WhenRelativePathEscapesProjectThenThrowsInvalidOperationException()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => AgentToolPathResolver.ResolvePath(() => _projectRoot, Path.Combine("..", "outside.cs")));

        Assert.Contains("inside the loaded project", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
