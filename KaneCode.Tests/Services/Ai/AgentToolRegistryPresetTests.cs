using KaneCode.Models;
using KaneCode.Services.Ai;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KaneCode.Tests.Services.Ai;

public sealed class AgentToolRegistryPresetTests
{
    private sealed class FakeTool : IAgentTool
    {
        public string Name { get; }
        public string Description { get; }
        public JsonElement ParametersSchema { get; }

        public FakeTool(string name, string description = "A test tool")
        {
            Name = name;
            Description = description;
            ParametersSchema = JsonDocument.Parse("""
                {
                    "type": "object",
                    "properties": {
                        "filePath": { "type": "string", "description": "The path" },
                        "mode": { "type": "string", "enum": ["fast", "safe"], "description": "Mode" }
                    },
                    "required": ["filePath"]
                }
                """).RootElement.Clone();
        }

        public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ToolCallResult.Ok("executed"));
        }
    }

    [Fact]
    public void WhenPresetHasDescriptionOverrideThenSerializedDescriptionUsesOverride()
    {
        AgentToolRegistry registry = new();
        registry.Register(new FakeTool("write", "default description"));

        AiPreset preset = new()
        {
            ToolDescriptions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["write"] = "overridden description"
            }
        };

        JsonElement result = registry.SerializeToolDefinitions(preset: preset);

        Assert.Equal("overridden description", result[0].GetProperty("function").GetProperty("description").GetString());
    }

    [Fact]
    public void WhenPresetHasNoDescriptionOverrideThenToolDescriptionIsUsed()
    {
        AgentToolRegistry registry = new();
        registry.Register(new FakeTool("read", "canonical read description"));

        JsonElement result = registry.SerializeToolDefinitions();

        Assert.Equal("canonical read description", result[0].GetProperty("function").GetProperty("description").GetString());
    }

    [Fact]
    public void WhenParameterIsPinnedThenDescriptionTokenResolvesToPinnedValue()
    {
        AgentToolRegistry registry = new();
        registry.Register(new FakeTool("write", "Write to {filePath} using {mode}"));

        AiPreset preset = new()
        {
            PinnedParameters = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal)
            {
                ["write"] = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["filePath"] = JsonDocument.Parse("\"src/out.txt\"").RootElement.Clone()
                }
            }
        };

        string description = registry.ResolveDescription(registry.Get("write")!, preset);

        Assert.Equal("Write to src/out.txt using {mode}", description);
    }

    [Fact]
    public void WhenParameterIsPinnedThenDefaultIsInjectedIntoParametersSchema()
    {
        AgentToolRegistry registry = new();
        registry.Register(new FakeTool("write"));

        AiPreset preset = new()
        {
            PinnedParameters = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal)
            {
                ["write"] = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["mode"] = JsonDocument.Parse("\"safe\"").RootElement.Clone()
                }
            }
        };

        JsonElement result = registry.SerializeToolDefinitions(preset: preset);
        JsonElement parameters = result[0].GetProperty("function").GetProperty("parameters");
        JsonElement modeProperty = parameters.GetProperty("properties").GetProperty("mode");

        Assert.Equal("\"safe\"", modeProperty.GetProperty("default").GetRawText());
    }

    [Fact]
    public void WhenNoParametersPinnedThenSchemaHasNoDefaults()
    {
        AgentToolRegistry registry = new();
        registry.Register(new FakeTool("write"));

        JsonElement result = registry.SerializeToolDefinitions();
        JsonElement parameters = result[0].GetProperty("function").GetProperty("parameters");
        JsonElement modeProperty = parameters.GetProperty("properties").GetProperty("mode");

        Assert.False(modeProperty.TryGetProperty("default", out _));
    }

    [Fact]
    public void WhenBuildingToolDefinitionThenJsonMatchesResolvedValues()
    {
        AgentToolRegistry registry = new();
        registry.Register(new FakeTool("write", "Write to {filePath}"));

        AiPreset preset = new()
        {
            ToolDescriptions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["write"] = "Custom write description"
            },
            PinnedParameters = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal)
            {
                ["write"] = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["filePath"] = JsonDocument.Parse("\"a.cs\"").RootElement.Clone()
                }
            }
        };

        JsonObject definition = registry.BuildToolDefinition(registry.Get("write")!, preset);

        Assert.Equal("function", definition["type"]!.GetValue<string>());
        JsonObject function = definition["function"]!.AsObject();
        Assert.Equal("write", function["name"]!.GetValue<string>());
        Assert.Equal("Custom write description", function["description"]!.GetValue<string>());
        Assert.Equal("a.cs", function["parameters"]!.AsObject()["properties"]!.AsObject()["filePath"]!.AsObject()["default"]!.GetValue<string>());
    }

    [Fact]
    public void WhenGettingParameterNamesThenReturnsDeclarationOrder()
    {
        AgentToolRegistry registry = new();
        registry.Register(new FakeTool("write"));

        IReadOnlyList<string> names = AgentToolRegistry.GetParameterNames(registry.Get("write")!);
        IReadOnlyList<string> required = AgentToolRegistry.GetRequiredParameters(registry.Get("write")!);

        Assert.Equal(["filePath", "mode"], names);
        Assert.Equal(["filePath"], required);
    }

    [Fact]
    public void WhenParameterIsHiddenThenItIsRemovedFromSerializedSchema()
    {
        AgentToolRegistry registry = new();
        registry.Register(new FakeTool("write"));

        AiPreset preset = new()
        {
            HiddenParameters = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
            {
                ["write"] = new HashSet<string>(StringComparer.Ordinal) { "mode" }
            }
        };

        JsonElement result = registry.SerializeToolDefinitions(preset: preset);
        JsonElement parameters = result[0].GetProperty("function").GetProperty("parameters");
        JsonElement properties = parameters.GetProperty("properties");

        Assert.True(properties.TryGetProperty("filePath", out _));
        Assert.False(properties.TryGetProperty("mode", out _));
        // The remaining required parameter is untouched.
        Assert.Equal("filePath", parameters.GetProperty("required")[0].GetString());
    }

    [Fact]
    public void WhenHiddenParameterIsAlsoPinnedThenItIsRemovedAndNoDefaultIsInjected()
    {
        AgentToolRegistry registry = new();
        registry.Register(new FakeTool("write"));

        AiPreset preset = new()
        {
            PinnedParameters = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal)
            {
                ["write"] = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["mode"] = JsonDocument.Parse("\"safe\"").RootElement.Clone()
                }
            },
            HiddenParameters = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
            {
                ["write"] = new HashSet<string>(StringComparer.Ordinal) { "mode" }
            }
        };

        JsonElement result = registry.SerializeToolDefinitions(preset: preset);
        JsonElement properties = result[0].GetProperty("function").GetProperty("parameters").GetProperty("properties");

        Assert.False(properties.TryGetProperty("mode", out _));
    }

    [Fact]
    public void WhenHiddenParameterIsRequiredThenItIsPrunedFromRequiredDefensively()
    {
        AgentToolRegistry registry = new();
        registry.Register(new FakeTool("write"));

        AiPreset preset = new()
        {
            HiddenParameters = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
            {
                ["write"] = new HashSet<string>(StringComparer.Ordinal) { "filePath", "mode" }
            }
        };

        JsonElement result = registry.SerializeToolDefinitions(preset: preset);
        JsonElement parameters = result[0].GetProperty("function").GetProperty("parameters");

        Assert.False(parameters.GetProperty("properties").TryGetProperty("filePath", out _));
        Assert.False(parameters.TryGetProperty("required", out _));
    }

    [Fact]
    public void WhenNoParametersHiddenThenSchemaIsUnchanged()
    {
        AgentToolRegistry registry = new();
        registry.Register(new FakeTool("write"));

        JsonElement result = registry.SerializeToolDefinitions();
        JsonElement parameters = result[0].GetProperty("function").GetProperty("parameters");

        Assert.True(parameters.GetProperty("properties").TryGetProperty("filePath", out _));
        Assert.True(parameters.GetProperty("properties").TryGetProperty("mode", out _));
        Assert.Equal("filePath", parameters.GetProperty("required")[0].GetString());
    }
}
