using System.Text.Json;
using KaneCode.Services.Ai;

namespace KaneCode.Tests.Services.Ai;

public class AgentToolRegistryTests
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
            ParametersSchema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();
        }

        public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ToolCallResult.Ok("executed"));
        }
    }

    [Fact]
    public void WhenNoToolsRegisteredThenHasToolsIsFalse()
    {
        AgentToolRegistry registry = new AgentToolRegistry();

        Assert.False(registry.HasTools);
    }

    [Fact]
    public void WhenToolRegisteredThenHasToolsIsTrue()
    {
        AgentToolRegistry registry = new AgentToolRegistry();

        registry.Register(new FakeTool("test_tool"));

        Assert.True(registry.HasTools);
    }

    [Fact]
    public void WhenToolRegisteredThenGetReturnsTool()
    {
        AgentToolRegistry registry = new AgentToolRegistry();
        FakeTool tool = new FakeTool("read_file");
        registry.Register(tool);

        IAgentTool? result = registry.Get("read_file");

        Assert.Same(tool, result);
    }

    [Fact]
    public void WhenToolNotRegisteredThenGetReturnsNull()
    {
        AgentToolRegistry registry = new AgentToolRegistry();

        IAgentTool? result = registry.Get("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public void WhenDuplicateToolRegisteredThenItReplacesExisting()
    {
        AgentToolRegistry registry = new AgentToolRegistry();
        FakeTool original = new FakeTool("my_tool", "original");
        FakeTool replacement = new FakeTool("my_tool", "replacement");
        registry.Register(original);

        registry.Register(replacement);

        IAgentTool? result = registry.Get("my_tool");
        Assert.Same(replacement, result);
    }

    [Fact]
    public void WhenRegisterAllCalledThenAllToolsAreRegistered()
    {
        AgentToolRegistry registry = new AgentToolRegistry();
        FakeTool tool1 = new FakeTool("tool_a");
        FakeTool tool2 = new FakeTool("tool_b");

        registry.RegisterAll([tool1, tool2]);

        Assert.NotNull(registry.Get("tool_a"));
        Assert.NotNull(registry.Get("tool_b"));
        Assert.Equal(2, registry.Tools.Count);
    }

    [Fact]
    public void WhenRegisterCalledWithNullThenThrowsArgumentNullException()
    {
        AgentToolRegistry registry = new AgentToolRegistry();

        Assert.Throws<ArgumentNullException>(() => registry.Register(null!));
    }

    [Fact]
    public void WhenRegisterAllCalledWithNullThenThrowsArgumentNullException()
    {
        AgentToolRegistry registry = new AgentToolRegistry();

        Assert.Throws<ArgumentNullException>(() => registry.RegisterAll(null!));
    }

    [Fact]
    public void WhenSerializeToolDefinitionsCalledThenReturnsValidJsonArray()
    {
        AgentToolRegistry registry = new AgentToolRegistry();
        registry.Register(new FakeTool("read_file", "Reads a file"));

        JsonElement result = registry.SerializeToolDefinitions();

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.Equal(1, result.GetArrayLength());

        JsonElement toolEntry = result[0];
        Assert.Equal("function", toolEntry.GetProperty("type").GetString());
        Assert.Equal("read_file", toolEntry.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("Reads a file", toolEntry.GetProperty("function").GetProperty("description").GetString());
    }

    [Fact]
    public void WhenSerializeToolDefinitionsWithFilterThenOnlyAllowedToolsAreIncluded()
    {
        AgentToolRegistry registry = new AgentToolRegistry();
        registry.Register(new FakeTool("tool_a"));
        registry.Register(new FakeTool("tool_b"));
        registry.Register(new FakeTool("tool_c"));

        JsonElement result = registry.SerializeToolDefinitions(["tool_a", "tool_c"]);

        Assert.Equal(2, result.GetArrayLength());
    }

    [Fact]
    public void WhenNoToolsRegisteredThenSerializeReturnsDefault()
    {
        AgentToolRegistry registry = new AgentToolRegistry();

        JsonElement result = registry.SerializeToolDefinitions();

        Assert.Equal(JsonValueKind.Undefined, result.ValueKind);
    }
}
