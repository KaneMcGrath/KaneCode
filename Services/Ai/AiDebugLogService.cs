using KaneCode.Models;
using System.Collections.ObjectModel;
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
            FormatArguments(argumentsJson),
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

    internal static string FormatArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return "(no arguments)";
        }

        string trimmedArguments = argumentsJson.Trim();

        try
        {
            using JsonDocument document = JsonDocument.Parse(trimmedArguments);
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
}
