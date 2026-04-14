using System.Text.Json;
using KaneCode.Services.Ai;

namespace KaneCode.Tests.Services.Ai;

public class AiChatModeRegistryTests
{
    private sealed class FakeMode : IAiChatMode
    {
        public string Id { get; }
        public string DisplayName { get; }
        public bool ToolsEnabled => false;

        public FakeMode(string id, string displayName)
        {
            Id = id;
            DisplayName = displayName;
        }

        public JsonElement GetToolDefinitions(AgentToolRegistry registry) => default;
        public string? BuildSystemPrompt(JsonElement toolsDef) => null;
    }

    [Fact]
    public void WhenNoModesRegisteredThenDefaultReturnsNull()
    {
        AiChatModeRegistry registry = new AiChatModeRegistry();

        Assert.Null(registry.Default);
    }

    [Fact]
    public void WhenModeRegisteredThenGetReturnsMode()
    {
        AiChatModeRegistry registry = new AiChatModeRegistry();
        FakeMode mode = new FakeMode("chat", "Chat");

        registry.Register(mode);

        Assert.Same(mode, registry.Get("chat"));
    }

    [Fact]
    public void WhenModeNotRegisteredThenGetReturnsNull()
    {
        AiChatModeRegistry registry = new AiChatModeRegistry();

        Assert.Null(registry.Get("nonexistent"));
    }

    [Fact]
    public void WhenFirstModeRegisteredThenDefaultReturnsIt()
    {
        AiChatModeRegistry registry = new AiChatModeRegistry();
        FakeMode mode = new FakeMode("chat", "Chat");

        registry.Register(mode);

        Assert.Same(mode, registry.Default);
    }

    [Fact]
    public void WhenMultipleModesRegisteredThenDefaultReturnsFirstRegisteredMode()
    {
        AiChatModeRegistry registry = new AiChatModeRegistry();
        FakeMode first = new FakeMode("agent", "Agent");
        FakeMode second = new FakeMode("chat", "Chat");

        registry.Register(first);
        registry.Register(second);

        Assert.Same(first, registry.Default);
    }

    [Fact]
    public void WhenDuplicateIdRegisteredThenExistingIsReplaced()
    {
        AiChatModeRegistry registry = new AiChatModeRegistry();
        FakeMode original = new FakeMode("chat", "Chat v1");
        FakeMode replacement = new FakeMode("chat", "Chat v2");
        registry.Register(original);

        registry.Register(replacement);

        IAiChatMode? result = registry.Get("chat");
        Assert.Same(replacement, result);
        Assert.Equal(1, registry.Modes.Count);
    }

    [Fact]
    public void WhenRegisterCalledWithNullThenThrowsArgumentNullException()
    {
        AiChatModeRegistry registry = new AiChatModeRegistry();

        Assert.Throws<ArgumentNullException>(() => registry.Register(null!));
    }

    [Fact]
    public void WhenModesRegisteredThenModesListContainsAll()
    {
        AiChatModeRegistry registry = new AiChatModeRegistry();
        registry.Register(new FakeMode("chat", "Chat"));
        registry.Register(new FakeMode("agent", "Agent"));
        registry.Register(new FakeMode("teacher", "Teacher"));

        Assert.Equal(3, registry.Modes.Count);
    }
}
