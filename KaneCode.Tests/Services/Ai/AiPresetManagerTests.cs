using KaneCode.Models;
using KaneCode.Services.Ai;

namespace KaneCode.Tests.Services.Ai;

public sealed class AiPresetManagerTests
{
    [Fact]
    public void WhenMultipleDefaultsThenFirstWinsAndOthersCleared()
    {
        AiPreset first = new() { Name = "First", IsDefault = true };
        AiPreset second = new() { Name = "Second", IsDefault = true };
        AiPreset third = new() { Name = "Third", IsDefault = true };

        AiPresetManager.NormalizeDefaults([first, second, third]);

        Assert.True(first.IsDefault);
        Assert.False(second.IsDefault);
        Assert.False(third.IsDefault);
    }

    [Fact]
    public void WhenSingleDefaultThenUnchanged()
    {
        AiPreset first = new() { Name = "First" };
        AiPreset second = new() { Name = "Second", IsDefault = true };

        AiPresetManager.NormalizeDefaults([first, second]);

        Assert.False(first.IsDefault);
        Assert.True(second.IsDefault);
    }

    [Fact]
    public void WhenNoDefaultsThenAllRemainFalse()
    {
        AiPreset first = new() { Name = "First" };
        AiPreset second = new() { Name = "Second" };

        AiPresetManager.NormalizeDefaults([first, second]);

        Assert.False(first.IsDefault);
        Assert.False(second.IsDefault);
    }
}
