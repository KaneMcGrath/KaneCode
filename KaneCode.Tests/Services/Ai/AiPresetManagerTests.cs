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

    [Fact]
    public void WhenCreatingDefaultSubagentPresetThenItIsMarkedAsSubagentWithBasicTools()
    {
        AiPreset preset = AiPresetManager.CreateDefaultSubagentPreset();

        Assert.Equal("Default Worker", preset.Name);
        Assert.True(preset.IsSubagent);
        Assert.False(string.IsNullOrWhiteSpace(preset.SubagentDescription));
        Assert.False(string.IsNullOrWhiteSpace(preset.SystemPrompt));
        Assert.NotNull(preset.AllowedTools);

        // Basic read/write/search tools are included…
        Assert.Contains("read", preset.AllowedTools!);
        Assert.Contains("write", preset.AllowedTools!);
        Assert.Contains("edit", preset.AllowedTools!);
        Assert.Contains("list", preset.AllowedTools!);
        Assert.Contains("search", preset.AllowedTools!);
        Assert.Contains("get_diagnostics", preset.AllowedTools!);

        // …but nothing beyond the file-system basics.
        Assert.DoesNotContain("git_commit", preset.AllowedTools!);
        Assert.DoesNotContain("build", preset.AllowedTools!);
        Assert.DoesNotContain("spawn_agent", preset.AllowedTools!);
    }
}
