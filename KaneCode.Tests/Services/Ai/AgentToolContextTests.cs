using KaneCode.Models;
using KaneCode.Services.Ai;
using System.Text.Json;

namespace KaneCode.Tests.Services.Ai;

public sealed class AgentToolContextTests
{
    private sealed class FakeOptionsTool : IAgentTool
    {
        public string Name => "fake";
        public string Description => "fake tool";
        public JsonElement ParametersSchema => JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone();

        public IReadOnlyDictionary<string, JsonElement> DefaultBackendOptions
        {
            get
            {
                return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["engine"] = JsonDocument.Parse("\"exact_match\"").RootElement.Clone(),
                    ["timeout"] = JsonDocument.Parse("30").RootElement.Clone(),
                    ["case_sensitive"] = JsonDocument.Parse("false").RootElement.Clone()
                };
            }
        }

        public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ToolCallResult.Ok("done"));
        }
    }

    [Fact]
    public void WhenNoPresetOverridesThenResolveReturnsDefaults()
    {
        IAgentTool tool = new FakeOptionsTool();

        IReadOnlyDictionary<string, JsonElement> resolved = AgentToolContext.Resolve(tool, preset: null);

        Assert.Equal("\"exact_match\"", resolved["engine"].GetRawText());
        Assert.Equal("30", resolved["timeout"].GetRawText());
        Assert.Equal("false", resolved["case_sensitive"].GetRawText());
    }

    [Fact]
    public void WhenPresetOverridesThenPresetWinsPerKey()
    {
        IAgentTool tool = new FakeOptionsTool();
        AiPreset preset = new()
        {
            ToolOptions = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal)
            {
                ["fake"] = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["engine"] = JsonDocument.Parse("\"anchored_replace\"").RootElement.Clone(),
                    ["timeout"] = JsonDocument.Parse("45").RootElement.Clone()
                }
            }
        };

        IReadOnlyDictionary<string, JsonElement> resolved = AgentToolContext.Resolve(tool, preset);

        Assert.Equal("\"anchored_replace\"", resolved["engine"].GetRawText());
        Assert.Equal("45", resolved["timeout"].GetRawText());
        // Un-overridden option keeps its default
        Assert.Equal("false", resolved["case_sensitive"].GetRawText());
    }

    [Fact]
    public void WhenPresetOverridesOtherToolThenDefaultsAreKept()
    {
        IAgentTool tool = new FakeOptionsTool();
        AiPreset preset = new()
        {
            ToolOptions = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal)
            {
                ["other"] = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["engine"] = JsonDocument.Parse("\"unified_diff\"").RootElement.Clone()
                }
            }
        };

        IReadOnlyDictionary<string, JsonElement> resolved = AgentToolContext.Resolve(tool, preset);

        Assert.Equal("\"exact_match\"", resolved["engine"].GetRawText());
    }

    [Fact]
    public void WhenOptionsPushedThenTypedGettersReadEffectiveValues()
    {
        AiPreset preset = new()
        {
            ToolOptions = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal)
            {
                ["fake"] = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["engine"] = JsonDocument.Parse("\"unified_diff\"").RootElement.Clone(),
                    ["timeout"] = JsonDocument.Parse("120").RootElement.Clone(),
                    ["case_sensitive"] = JsonDocument.Parse("true").RootElement.Clone()
                }
            }
        };

        IReadOnlyDictionary<string, JsonElement> resolved = AgentToolContext.Resolve(new FakeOptionsTool(), preset);
        using (AgentToolContext.Push(resolved))
        {
            Assert.Equal("unified_diff", AgentToolContext.GetString("engine", "exact_match"));
            Assert.Equal(120, AgentToolContext.GetInt("timeout", 30));
            Assert.True(AgentToolContext.GetBool("case_sensitive", false));
            Assert.Equal("fallback", AgentToolContext.GetString("missing", "fallback"));
        }

        // After the scope is disposed, the context is cleared
        Assert.Equal("exact_match", AgentToolContext.GetString("engine", "exact_match"));
    }
}
