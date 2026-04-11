using KaneCode.Models;
using KaneCode.Services.Ai;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KaneCode.Tests.Services.Ai;

public sealed class AiConversationStoreTests : IDisposable
{
    private readonly string _projectKey = $"conversation-store-test-{Guid.NewGuid():N}";

    public void Dispose()
    {
        AiConversationStore.Clear(_projectKey);
    }

    [Fact]
    public void WhenSavingConversationStateThenMessagesAndReferencesRoundTrip()
    {
        AiConversation conversation = new()
        {
            Title = "Investigate build failures",
            ProjectContextInjected = true
        };
        conversation.Messages.Add(new AiChatMessage(AiChatRole.System, "Project context"));
        conversation.Messages.Add(new AiChatMessage(AiChatRole.User, "Please inspect Program.cs"));
        conversation.References.Add(new AiChatReference(AiReferenceKind.File, @"K:\Project\Program.cs")
        {
            Content = "class Program { }"
        });

        AiConversationState state = new()
        {
            ActiveConversationId = conversation.Id
        };
        state.Conversations.Add(conversation);

        AiConversationStore.SaveState(_projectKey, state);

        AiConversationState loadedState = AiConversationStore.LoadState(_projectKey);

        Assert.Equal(conversation.Id, loadedState.ActiveConversationId);
        Assert.Single(loadedState.Conversations);
        Assert.Equal("Investigate build failures", loadedState.Conversations[0].Title);
        Assert.True(loadedState.Conversations[0].ProjectContextInjected);
        Assert.Equal(2, loadedState.Conversations[0].Messages.Count);
        Assert.Single(loadedState.Conversations[0].References);
        Assert.Equal("class Program { }", loadedState.Conversations[0].References[0].Content);
    }

    [Fact]
    public void WhenLoadingLegacyHistoryThenSingleConversationIsCreated()
    {
        string filePath = GetHistoryFilePath(_projectKey);
        string? directoryPath = Path.GetDirectoryName(filePath);
        Assert.False(string.IsNullOrWhiteSpace(directoryPath));
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new InvalidOperationException("The conversation history directory path should not be empty.");
        }

        Directory.CreateDirectory(directoryPath);

        object[] legacyMessages =
        [
            new
            {
                Role = nameof(AiChatRole.System),
                Content = "Project context",
                ThinkingContent = (string?)null,
                ToolCallId = (string?)null,
                ToolCalls = (object[]?)null
            },
            new
            {
                Role = nameof(AiChatRole.User),
                Content = "Explain this class",
                ThinkingContent = (string?)null,
                ToolCallId = (string?)null,
                ToolCalls = (object[]?)null
            }
        ];

        File.WriteAllText(filePath, JsonSerializer.Serialize(legacyMessages));

        AiConversationState loadedState = AiConversationStore.LoadState(_projectKey);

        Assert.Single(loadedState.Conversations);
        Assert.Equal("Imported conversation", loadedState.Conversations[0].Title);
        Assert.Equal(2, loadedState.Conversations[0].Messages.Count);
        Assert.True(loadedState.Conversations[0].ProjectContextInjected);
        Assert.Equal(loadedState.Conversations[0].Id, loadedState.ActiveConversationId);
    }

    private static string GetHistoryFilePath(string projectKey)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectKey))).ToLowerInvariant();
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KaneCode",
            "ai-chat-history",
            $"{hash}.json");
    }
}
