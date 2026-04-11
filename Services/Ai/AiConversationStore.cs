using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KaneCode.Models;

namespace KaneCode.Services.Ai;

/// <summary>
/// Persists AI conversations per project key under LocalAppData.
/// </summary>
internal static class AiConversationStore
{
    private static readonly string HistoryDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KaneCode",
        "ai-chat-history");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    internal static AiConversationState LoadState(string projectKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);

        string filePath = GetHistoryFilePath(projectKey);
        if (!File.Exists(filePath))
        {
            return new AiConversationState();
        }

        string json = File.ReadAllText(filePath);
        return DeserializeState(json);
    }

    internal static void SaveState(string projectKey, AiConversationState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);
        ArgumentNullException.ThrowIfNull(state);

        Directory.CreateDirectory(HistoryDirectory);

        string filePath = GetHistoryFilePath(projectKey);
        PersistedConversationState persistedState = new()
        {
            ActiveConversationId = state.ActiveConversationId,
            Conversations = state.Conversations.Select(MapConversation).ToList()
        };

        string json = JsonSerializer.Serialize(persistedState, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    internal static void Clear(string projectKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);

        var filePath = GetHistoryFilePath(projectKey);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    private static string GetHistoryFilePath(string projectKey)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectKey))).ToLowerInvariant();
        return Path.Combine(HistoryDirectory, $"{hash}.json");
    }

    private static AiConversationState DeserializeState(string json)
    {
        try
        {
            PersistedConversationState? persistedState = JsonSerializer.Deserialize<PersistedConversationState>(json);
            if (persistedState is not null && persistedState.Conversations is not null)
            {
                AiConversationState state = new()
                {
                    ActiveConversationId = persistedState.ActiveConversationId
                };

                foreach (PersistedConversationRecord conversation in persistedState.Conversations)
                {
                    state.Conversations.Add(MapConversation(conversation));
                }

                return state;
            }
        }
        catch (JsonException)
        {
        }

        return MigrateLegacyState(json);
    }

    private static AiConversationState MigrateLegacyState(string json)
    {
        List<PersistedConversationRecordMessage> records = JsonSerializer.Deserialize<List<PersistedConversationRecordMessage>>(json) ?? [];
        List<AiChatMessage> messages = records.Select(record => MapMessage(record)).ToList();
        AiConversation conversation = new()
        {
            Title = "Imported conversation",
            ProjectContextInjected = messages.Any(message => message.Role == AiChatRole.System)
        };
        conversation.Messages.AddRange(messages);

        AiConversationState state = new()
        {
            ActiveConversationId = conversation.Id
        };
        state.Conversations.Add(conversation);
        return state;
    }

    private static PersistedConversationRecord MapConversation(AiConversation conversation)
    {
        return new PersistedConversationRecord
        {
            Id = conversation.Id,
            Title = conversation.Title,
            CreatedUtc = conversation.CreatedUtc,
            UpdatedUtc = conversation.UpdatedUtc,
            ProjectContextInjected = conversation.ProjectContextInjected,
            Messages = conversation.Messages.Select(MapMessage).ToList(),
            References = conversation.References.Select(MapReference).ToList()
        };
    }

    private static PersistedConversationRecordMessage MapMessage(AiChatMessage message)
    {
        return new PersistedConversationRecordMessage
        {
            Role = message.Role.ToString(),
            Content = message.Content,
            ThinkingContent = message.ThinkingContent,
            ToolCallId = message.ToolCallId,
            ToolCalls = message.ToolCalls?.Select(tc => new AiToolCallRequestRecord
            {
                Id = tc.Id,
                FunctionName = tc.FunctionName,
                ArgumentsJson = tc.ArgumentsJson
            }).ToList()
        };
    }

    private static PersistedReferenceRecord MapReference(AiChatReference reference)
    {
        return new PersistedReferenceRecord
        {
            Kind = reference.Kind.ToString(),
            FullPath = reference.FullPath,
            DisplayName = reference.DisplayName,
            Content = reference.Content
        };
    }

    private static AiConversation MapConversation(PersistedConversationRecord conversation)
    {
        AiConversation mappedConversation = new()
        {
            Id = string.IsNullOrWhiteSpace(conversation.Id) ? Guid.NewGuid().ToString("N") : conversation.Id,
            Title = string.IsNullOrWhiteSpace(conversation.Title) ? "New conversation" : conversation.Title,
            CreatedUtc = conversation.CreatedUtc == default ? DateTimeOffset.UtcNow : conversation.CreatedUtc,
            UpdatedUtc = conversation.UpdatedUtc == default ? DateTimeOffset.UtcNow : conversation.UpdatedUtc,
            ProjectContextInjected = conversation.ProjectContextInjected
        };
        mappedConversation.Messages.AddRange((conversation.Messages ?? []).Select(MapMessage));
        mappedConversation.References.AddRange((conversation.References ?? []).Select(MapReference));
        return mappedConversation;
    }

    private static AiChatMessage MapMessage(PersistedConversationRecordMessage record)
    {
        return new AiChatMessage(ParseRole(record.Role), record.Content)
        {
            ThinkingContent = record.ThinkingContent,
            ToolCallId = record.ToolCallId,
            ToolCalls = record.ToolCalls?.Select(tc => new AiToolCallRequest(tc.Id, tc.FunctionName, tc.ArgumentsJson)).ToList()
        };
    }

    private static AiChatReference MapReference(PersistedReferenceRecord record)
    {
        AiReferenceKind kind = ParseReferenceKind(record.Kind);
        return new AiChatReference(kind, record.FullPath, record.DisplayName)
        {
            Content = record.Content
        };
    }

    private static AiChatRole ParseRole(string role)
    {
        return role switch
        {
            nameof(AiChatRole.System) => AiChatRole.System,
            nameof(AiChatRole.User) => AiChatRole.User,
            nameof(AiChatRole.Assistant) => AiChatRole.Assistant,
            nameof(AiChatRole.Tool) => AiChatRole.Tool,
            _ => throw new InvalidDataException($"Unknown chat role '{role}'.")
        };
    }

    private static AiReferenceKind ParseReferenceKind(string kind)
    {
        return kind switch
        {
            nameof(AiReferenceKind.File) => AiReferenceKind.File,
            nameof(AiReferenceKind.CurrentDocument) => AiReferenceKind.CurrentDocument,
            nameof(AiReferenceKind.OpenDocuments) => AiReferenceKind.OpenDocuments,
            nameof(AiReferenceKind.BuildOutput) => AiReferenceKind.BuildOutput,
            nameof(AiReferenceKind.Class) => AiReferenceKind.Class,
            nameof(AiReferenceKind.ExternalFolder) => AiReferenceKind.ExternalFolder,
            _ => throw new InvalidDataException($"Unknown reference kind '{kind}'.")
        };
    }

    private sealed class PersistedConversationState
    {
        public string? ActiveConversationId { get; init; }

        public List<PersistedConversationRecord> Conversations { get; init; } = [];
    }

    private sealed class PersistedConversationRecord
    {
        public string Id { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        public DateTimeOffset CreatedUtc { get; init; }

        public DateTimeOffset UpdatedUtc { get; init; }

        public bool ProjectContextInjected { get; init; }

        public List<PersistedConversationRecordMessage>? Messages { get; init; }

        public List<PersistedReferenceRecord>? References { get; init; }
    }

    private sealed class PersistedReferenceRecord
    {
        public string Kind { get; init; } = string.Empty;

        public string FullPath { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public string Content { get; init; } = string.Empty;
    }

    private class PersistedConversationRecordMessage
    {
        public string Role { get; init; } = string.Empty;

        public string Content { get; init; } = string.Empty;

        public string? ThinkingContent { get; init; }

        public string? ToolCallId { get; init; }

        public List<AiToolCallRequestRecord>? ToolCalls { get; init; }
    }
    private sealed class AiToolCallRequestRecord
    {
        public string Id { get; init; } = string.Empty;

        public string FunctionName { get; init; } = string.Empty;

        public string ArgumentsJson { get; init; } = string.Empty;
    }
}
