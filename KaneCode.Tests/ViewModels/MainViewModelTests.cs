using KaneCode.ViewModels;

namespace KaneCode.Tests.ViewModels;

public class MainViewModelTests
{
    [Theory]
    [InlineData(@"C:\repo\App.csproj")]
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
}
