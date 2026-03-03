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

        var filePath = GetHistoryFilePath(projectKey);
        if (!File.Exists(filePath))
        {
            return [];
        }

        var json = File.ReadAllText(filePath);
        var records = JsonSerializer.Deserialize<List<AiChatMessageRecord>>(json) ?? [];

        return records
            .Select(r => new AiChatMessage(ParseRole(r.Role), r.Content))
            .ToList();
    }

    internal static void Save(string projectKey, IReadOnlyList<AiChatMessage> messages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);
        ArgumentNullException.ThrowIfNull(messages);

        Directory.CreateDirectory(HistoryDirectory);

        var filePath = GetHistoryFilePath(projectKey);

        var records = messages
            .Select(m => new AiChatMessageRecord(m.Role.ToString(), m.Content))
            .ToList();

        var json = JsonSerializer.Serialize(records, JsonOptions);
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
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectKey))).ToLowerInvariant();
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

    private sealed record AiChatMessageRecord(string Role, string Content);
}
