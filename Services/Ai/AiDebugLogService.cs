using KaneCode.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;

namespace KaneCode.Services.Ai;

/// <summary>
/// Stores AI debug information that can be surfaced in the debug UI.
/// </summary>
internal sealed class AiDebugLogService
{
    private const int MaxToolFailureEntries = 200;

    public ObservableCollection<AiToolFailureEntry> ToolFailures { get; } = [];

    public void LogToolFailure(string toolName, string? argumentsJson, string? error, string? toolCallId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        string normalizedError = string.IsNullOrWhiteSpace(error)
            ? "Unknown tool failure."
            : error.Trim();
        string normalizedToolCallId = string.IsNullOrWhiteSpace(toolCallId)
            ? string.Empty
            : toolCallId.Trim();

        ToolFailures.Insert(0, new AiToolFailureEntry(
            DateTimeOffset.Now,
            toolName,
            normalizedError,
            FormatArguments(toolName, argumentsJson),
            normalizedToolCallId));

        while (ToolFailures.Count > MaxToolFailureEntries)
        {
            ToolFailures.RemoveAt(ToolFailures.Count - 1);
        }
    }

    public void ClearToolFailures()
    {
        ToolFailures.Clear();
    }

    internal string ExportToolFailureEntry(AiToolFailureEntry entry)
    {
        return ExportToolFailureEntry(entry, Path.GetTempPath());
    }

    internal static string ExportToolFailureEntry(AiToolFailureEntry entry, string directoryPath)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("The export directory path cannot be blank.", nameof(directoryPath));
        }

        Directory.CreateDirectory(directoryPath);

        string filePath = Path.Combine(directoryPath, BuildToolFailureExportFileName(entry));
        File.WriteAllText(filePath, BuildToolFailureExportContents(entry));
        return filePath;
    }

    internal static string FormatArguments(string toolName, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return "(no arguments)";
        }

        string trimmedArguments = argumentsJson.Trim();

        try
        {
            using JsonDocument document = AgentToolArgumentsParser.Parse(toolName, trimmedArguments);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (JsonException)
        {
            return trimmedArguments;
        }
    }

    private static string BuildToolFailureExportContents(AiToolFailureEntry entry)
    {
        string callId = string.IsNullOrWhiteSpace(entry.ToolCallId)
            ? "(none)"
            : entry.ToolCallId;

        StringBuilder builder = new();
        builder.AppendLine("AI tool failure");
        builder.AppendLine($"Timestamp: {entry.Timestamp:O}");
        builder.AppendLine($"Tool: {entry.ToolName}");
        builder.AppendLine($"Call Id: {callId}");
        builder.AppendLine();
        builder.AppendLine("Error:");
        builder.AppendLine(entry.Error);
        builder.AppendLine();
        builder.AppendLine("Arguments:");
        builder.AppendLine(entry.Arguments);
        return builder.ToString();
    }

    private static string BuildToolFailureExportFileName(AiToolFailureEntry entry)
    {
        string safeToolName = SanitizeFileNamePart(entry.ToolName);
        string timestamp = entry.Timestamp.ToLocalTime().ToString("yyyyMMdd-HHmmssfff");
        return $"ai-tool-failure-{timestamp}-{safeToolName}-{Guid.NewGuid():N}.txt";
    }

    private static string SanitizeFileNamePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "tool";
        }

        StringBuilder builder = new(value.Length);

        foreach (char character in value.Trim())
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), character) >= 0)
            {
                builder.Append('-');
                continue;
            }

            builder.Append(char.IsWhiteSpace(character) ? '-' : character);
        }

        return builder.Length == 0 ? "tool" : builder.ToString();
    }
}
