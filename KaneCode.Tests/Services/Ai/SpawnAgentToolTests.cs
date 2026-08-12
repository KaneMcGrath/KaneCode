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

    [Fact]
    public void WhenBackendOptionsSchemaIsBuiltThenItExposesAllowedPresetsArray()
    {
        SpawnAgentTool tool = new();

        JsonElement backend = tool.BackendOptionsSchema;
        JsonElement properties = backend.GetProperty("properties");

        Assert.True(properties.TryGetProperty("allowed_presets", out JsonElement allowed));
        Assert.Equal("array", allowed.GetProperty("type").GetString());
        Assert.Equal("string", allowed.GetProperty("items").GetProperty("type").GetString());
        Assert.True(allowed.GetProperty("items").TryGetProperty("enum", out _));
    }

    [Fact]
    public void WhenParentPresetAllowsPresetsThenOnlyThoseAreAllowed()
    {
        AiPreset parent = new()
        {
            ToolOptions = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal)
            {
                ["spawn_agent"] = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["allowed_presets"] = JsonDocument.Parse("[\"Code Reviewer\", \"Tester\"]").RootElement.Clone()
                }
            }
        };

        HashSet<string>? allowed = SpawnAgentTool.GetAllowedSubagentPresets(parent);

        Assert.NotNull(allowed);
        Assert.Equal(2, allowed.Count);
        Assert.Contains("Code Reviewer", allowed);
        Assert.Contains("Tester", allowed);
    }

    [Fact]
    public void WhenParentPresetHasNoAllowListThenAllPresetsAreAllowed()
    {
        AiPreset parent = new();

        HashSet<string>? allowed = SpawnAgentTool.GetAllowedSubagentPresets(parent);

        Assert.Null(allowed);
    }

    [Fact]
    public void WhenParentPresetBlocksAllThenEmptySetIsReturned()
    {
        AiPreset parent = new()
        {
            ToolOptions = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal)
            {
                ["spawn_agent"] = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["allowed_presets"] = JsonDocument.Parse("[]").RootElement.Clone()
                }
            }
        };

        HashSet<string>? allowed = SpawnAgentTool.GetAllowedSubagentPresets(parent);

        Assert.NotNull(allowed);
        Assert.Empty(allowed);
    }

    [Fact]
    public void WhenAllowListIsPresentThenMatchingIsCaseInsensitive()
    {
        AiPreset parent = new()
        {
            ToolOptions = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal)
            {
                ["spawn_agent"] = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["allowed_presets"] = JsonDocument.Parse("[\"code reviewer\"]").RootElement.Clone()
                }
            }
        };

        HashSet<string>? allowed = SpawnAgentTool.GetAllowedSubagentPresets(parent);

        Assert.NotNull(allowed);
        Assert.Contains("CODE REVIEWER", allowed);
    }
}
