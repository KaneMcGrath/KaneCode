using KaneCode.ViewModels;

namespace KaneCode.Tests.ViewModels;

public class MainViewModelTests
{
    [Theory]
    [InlineData(@"C:\repo\App.csproj")]
    [InlineData(@"C:\repo\App.slnx")]
    [InlineData(@"C:\repo\Directory.Build.props")]
    [InlineData(@"C:\repo\Directory.Build.targets")]
    [InlineData(@"C:\repo\Directory.Packages.props")]
    public void WhenPathIsProjectConfigFileThenIsProjectConfigFileReturnsTrue(string filePath)
    {
        Assert.True(MainViewModel.IsProjectConfigFile(filePath));
    }

    [Theory]
    [InlineData(@"C:\repo\obj\Debug\net8.0\Generated.cs")]
    [InlineData(@"C:\repo\bin\Debug\net8.0\App.dll")]
    [InlineData(@"C:\repo\src\Program.cs")]
    public void WhenPathIsBuildOutputOrSourceFileThenIsProjectConfigFileReturnsFalse(string filePath)
    {
        Assert.False(MainViewModel.IsProjectConfigFile(filePath));
    }

    [Fact]
    public void WhenBuildingPeekContentThenTargetLineIsMarkedAndContextIncluded()
    {
        string fileText = "first\nsecond\nthird\nfourth";

        string preview = MainViewModel.BuildPeekContent(fileText, 3, 1);

        Assert.Equal("     2: second\r\n>    3: third\r\n     4: fourth", preview);
    }

    [Fact]
    public void WhenExternalUntrackedCSharpFileChangesInLoadedWorkspaceThenReloadIsRequired()
    {
        bool result = MainViewModel.ShouldReloadWorkspaceForExternalCSharpFileChange(
            @"C:\repo\Services\NewService.cs",
            hasLoadedProjects: true,
            isTrackedDocument: false,
            loadedProjectOrSolutionPath: @"C:\repo\App.csproj");

        Assert.True(result);
    }

    [Theory]
    [InlineData(@"C:\repo\Services\ExistingService.cs", true, true, @"C:\repo\App.csproj")]
    [InlineData(@"C:\repo\notes.txt", true, false, @"C:\repo\App.csproj")]
    [InlineData(@"C:\repo\Services\NewService.cs", false, false, @"C:\repo\App.csproj")]
    [InlineData(@"C:\repo\Services\NewService.cs", true, false, null)]
    public void WhenFileChangeDoesNotRequireProjectReloadThenHelperReturnsFalse(
        string filePath,
        bool hasLoadedProjects,
        bool isTrackedDocument,
        string? loadedProjectOrSolutionPath)
    {
        bool result = MainViewModel.ShouldReloadWorkspaceForExternalCSharpFileChange(
            filePath,
            hasLoadedProjects,
            isTrackedDocument,
            loadedProjectOrSolutionPath);

        Assert.False(result);
    }
}
