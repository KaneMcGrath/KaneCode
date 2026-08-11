using KaneCode.Models;
using KaneCode.Services.Ai.Agents;
using System.Text.Json;

namespace KaneCode.Tests.Services.Ai;

public sealed class SpawnAgentToolTests
{
    [Fact]
    public void WhenSchemaIsBuiltThenItAcceptsPresetByNameAndNotMode()
    {
        SpawnAgentTool tool = new();

        JsonElement parameters = tool.ParametersSchema;
        JsonElement properties = parameters.GetProperty("properties");

        Assert.True(properties.TryGetProperty("preset", out _));
        Assert.False(properties.TryGetProperty("mode", out _));
        Assert.Equal("task", parameters.GetProperty("required")[0].GetString());
    }

    [Fact]
    public void WhenNoSubagentPresetsThenDescriptionMentionsEditor()
    {
        string description = SpawnAgentTool.BuildDescription("base guidance", []);

        Assert.StartsWith("base guidance", description);
        Assert.Contains("No subagent presets are configured", description);
    }

    [Fact]
    public void WhenSubagentPresetsExistThenDescriptionListsNameAndDescription()
    {
        AiPreset reviewer = new() { Name = "Code Reviewer", SubagentDescription = "Reviews diffs" };
        AiPreset tester = new() { Name = "Tester", SubagentDescription = "Runs tests" };

        string description = SpawnAgentTool.BuildDescription("base guidance", [reviewer, tester]);

        Assert.Contains("- Code Reviewer — Reviews diffs", description);
        Assert.Contains("- Tester — Runs tests", description);
    }

    [Fact]
    public void WhenSubagentPresetHasNoDescriptionThenOnlyNameIsListed()
    {
        AiPreset bare = new() { Name = "Bare" };

        string description = SpawnAgentTool.BuildDescription("base guidance", [bare]);

        Assert.Contains("- Bare", description);
        Assert.DoesNotContain("—", description);
    }

    [Fact]
    public void WhenPresetParameterDescriptionIsBuiltThenItListsSubagentNames()
    {
        AiPreset reviewer = new() { Name = "Code Reviewer", IsSubagent = true };
        AiPreset regular = new() { Name = "Regular" };

        string description = SpawnAgentTool.BuildPresetParameterDescription([reviewer, regular]);

        Assert.Contains("Code Reviewer", description);
        Assert.DoesNotContain("Regular", description);
    }

    [Fact]
    public void WhenNoSubagentPresetsThenPresetParameterDescriptionMentionsEditor()
    {
        string description = SpawnAgentTool.BuildPresetParameterDescription([]);

        Assert.Contains("No subagent presets are currently configured", description);
    }
}
