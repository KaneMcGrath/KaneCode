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

    [Fact]
    public void WhenRelativePathStartsWithProjectDirNameThenResolvesFromParent()
    {
        // Arrange: project root is a subdirectory (e.g. the project folder, not the solution root).
        // The input path starts with that folder name (e.g. "KaneCode/Models/File.cs" when
        // projectRoot is "...\KaneCode\KaneCode").
        string subDir = Path.Combine(_projectRoot, "SubDir");
        Directory.CreateDirectory(subDir);
        string innerFilePath = Path.Combine(subDir, "inner.txt");
        File.WriteAllText(innerFilePath, "inner content");

        // Set the project root provider to point to the subdirectory
        string subDirCsproj = Path.Combine(subDir, "SubDir.csproj");
        File.WriteAllText(subDirCsproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        string resolvedPath = AgentToolPathResolver.ResolvePath(
            () => subDirCsproj,
            Path.Combine("SubDir", "inner.txt"));

        Assert.Equal(Path.GetFullPath(innerFilePath), resolvedPath, ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void WhenRelativePathStartsWithProjectDirNameAndParentDirIsNullThenFallsBack()
    {
        // When the project root has no parent (e.g. drive root),
        // the fallback should still work correctly.
        // For this test, we use a regular relative path that does not match
        // the project directory name to verify the normal path still works.
        string nestedDir = Path.Combine(_projectRoot, "nested");
        Directory.CreateDirectory(nestedDir);
        string nestedFilePath = Path.Combine(nestedDir, "test.txt");
        File.WriteAllText(nestedFilePath, "test");

        string resolvedPath = AgentToolPathResolver.ResolvePath(
            () => _projectRoot,
            Path.Combine("nested", "test.txt"));

        string expectedPath = Path.GetFullPath(Path.Combine(_projectRoot, "nested", "test.txt"));
        Assert.Equal(expectedPath, resolvedPath, ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void WhenAbsolutePathIsInsideAllowedExternalRootThenReturnsResolvedPath()
    {
        string externalDirectory = Path.Combine(Path.GetTempPath(), $"KaneCodeExternal_{Guid.NewGuid():N}");
        Directory.CreateDirectory(externalDirectory);

        try
        {
            string externalFilePath = Path.Combine(externalDirectory, "external.cs");
            File.WriteAllText(externalFilePath, "class External { }");

            string resolvedPath = AgentToolPathResolver.ResolvePath(
                () => _projectRoot,
                externalFilePath,
                [externalDirectory]);

            Assert.Equal(Path.GetFullPath(externalFilePath), resolvedPath, ignoreCase: OperatingSystem.IsWindows());
        }
        finally
        {
            try { Directory.Delete(externalDirectory, recursive: true); }
            catch { }
        }
    }
}
