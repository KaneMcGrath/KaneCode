using KaneCode.Models;
using KaneCode.Services;

namespace KaneCode.Tests.Models;

public class RunProfileOptionTests
{
    [Fact]
    public void WhenDefaultOptionThenIsDefaultFallbackIsTrue()
    {
        RunProfileOption option = RunProfileOption.CreateDefault();

        Assert.True(option.IsDefaultFallback);
        Assert.Equal("(Default)", option.DisplayName);
        Assert.Null(option.Profile);
    }

    [Fact]
    public void WhenWrappingProfileThenIsDefaultFallbackIsFalse()
    {
        LaunchProfile profile = new() { Name = "MyApp" };
        RunProfileOption option = new(profile, "MyApp");

        Assert.False(option.IsDefaultFallback);
        Assert.Equal("MyApp", option.DisplayName);
        Assert.Same(profile, option.Profile);
    }
}
