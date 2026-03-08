using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KaneCode.Services.Ai;

/// <summary>
/// Persists AI conversation history per project key under LocalAppData.
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

    internal static IReadOnlyList<AiChatMessage> Load(string projectKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);

        string filePath = GetHistoryFilePath(projectKey);
        if (!File.Exists(filePath))
        {
            return [];
        }

        string json = File.ReadAllText(filePath);
        List<AiChatMessageRecord> records = JsonSerializer.Deserialize<List<AiChatMessageRecord>>(json) ?? [];

        return records
            .Select(r => new AiChatMessage(ParseRole(r.Role), r.Content)
            {
                ThinkingContent = r.ThinkingContent,
                ToolCallId = r.ToolCallId,
                ToolCalls = r.ToolCalls?.Select(tc => new AiToolCallRequest(tc.Id, tc.FunctionName, tc.ArgumentsJson)).ToList()
            })
            .ToList();
    }

    internal static void Save(string projectKey, IReadOnlyList<AiChatMessage> messages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);
        ArgumentNullException.ThrowIfNull(messages);

        Directory.CreateDirectory(HistoryDirectory);

        string filePath = GetHistoryFilePath(projectKey);

        List<AiChatMessageRecord> records = messages
            .Select(m => new AiChatMessageRecord
            {
                Role = m.Role.ToString(),
                Content = m.Content,
                ThinkingContent = m.ThinkingContent,
                ToolCallId = m.ToolCallId,
                ToolCalls = m.ToolCalls?.Select(tc => new AiToolCallRequestRecord
                {
                    Id = tc.Id,
                    FunctionName = tc.FunctionName,
                    ArgumentsJson = tc.ArgumentsJson
                }).ToList()
            })
            .ToList();

        string json = JsonSerializer.Serialize(records, JsonOptions);
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

    private sealed class AiChatMessageRecord
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
