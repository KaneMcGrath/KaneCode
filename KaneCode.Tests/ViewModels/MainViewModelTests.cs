using KaneCode.ViewModels;

namespace KaneCode.Tests.ViewModels;

public class MainViewModelTests
{
    [Fact]
    public void WhenProjectIsNotRunningThenRunCommandTextShowsRun()
    {
        string result = MainViewModel.GetRunCommandText(isProjectRunInProgress: false);

        Assert.Equal("_Run Project", result);
    }

    [Fact]
    public void WhenProjectIsRunningThenRunCommandTextShowsStop()
    {
        string result = MainViewModel.GetRunCommandText(isProjectRunInProgress: true);

        Assert.Equal("_Stop Project", result);
    }

    [Fact]
    public void WhenProjectIsNotRunningThenQuickButtonTextShowsPlaySymbol()
    {
        string result = MainViewModel.GetRunQuickButtonText(isProjectRunInProgress: false);

        Assert.Equal("\u25B6", result);
    }

    [Fact]
    public void WhenProjectIsRunningThenQuickButtonTextShowsStopSymbol()
    {
        string result = MainViewModel.GetRunQuickButtonText(isProjectRunInProgress: true);

        Assert.Equal("\u25A0", result);
    }

    [Fact]
    public void WhenProjectIsRunningThenStatusTextShowsRunningProject()
    {
        string result = MainViewModel.GetStatusText(
            loadingStatus: null,
            isProjectRunInProgress: true,
            runningProjectPath: @"C:\repo\App.csproj",
            activeTabFilePath: @"C:\repo\Program.cs",
            diagnosticStatusText: "1 error(s), 0 warning(s)");

        Assert.Equal("Running: C:\\repo\\App.csproj", result);
    }

    [Fact]
    public void WhenProjectIsNotRunningThenStatusTextShowsEditingAndDiagnostics()
    {
        string result = MainViewModel.GetStatusText(
            loadingStatus: null,
            isProjectRunInProgress: false,
            runningProjectPath: null,
            activeTabFilePath: @"C:\repo\Program.cs",
            diagnosticStatusText: "1 error(s), 0 warning(s)");

        Assert.Equal("Editing: C:\\repo\\Program.cs  |  1 error(s), 0 warning(s)", result);
    }

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
