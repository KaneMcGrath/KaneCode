using System.IO;
using System.Text;
using System.Text.Json;

namespace KaneCode.Services.Ai;

/// <summary>
/// Parses tool argument payloads and normalizes known malformed formats.
/// </summary>
internal static class AgentToolArgumentsParser
{
    private const string ReadFileToolName = "read_file";

    public static JsonDocument Parse(string toolName, string argumentsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(argumentsJson);

        string trimmedArguments = argumentsJson.Trim();

        try
        {
            return JsonDocument.Parse(trimmedArguments);
        }
        catch (JsonException) when (string.Equals(toolName, ReadFileToolName, StringComparison.Ordinal) &&
                                    TryNormalizeConsecutiveReadFileArguments(trimmedArguments, out JsonDocument? normalizedDocument))
        {
            return normalizedDocument;
        }
    }

    public static bool TryParse(string toolName, string? argumentsJson, out JsonDocument? document)
    {
        document = null;

        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return false;
        }

        try
        {
            document = Parse(toolName, argumentsJson);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryNormalizeConsecutiveReadFileArguments(string argumentsJson, out JsonDocument? document)
    {
        document = null;

        if (!TryExtractReadFilePaths(argumentsJson, out List<string> filePaths) || filePaths.Count <= 1)
        {
            return false;
        }

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("filePaths");

            foreach (string filePath in filePaths)
            {
                writer.WriteStringValue(filePath);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        document = JsonDocument.Parse(stream.ToArray());
        return true;
    }

    private static bool TryExtractReadFilePaths(string argumentsJson, out List<string> filePaths)
    {
        filePaths = [];

        try
        {
            byte[] jsonBytes = Encoding.UTF8.GetBytes(argumentsJson);
            Utf8JsonReader reader = new(jsonBytes, new JsonReaderOptions
            {
                AllowMultipleValues = true
            });

            while (reader.Read())
            {
                using JsonDocument valueDocument = JsonDocument.ParseValue(ref reader);
                JsonElement root = valueDocument.RootElement;

                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("filePath", out JsonElement filePathElement) ||
                    filePathElement.ValueKind != JsonValueKind.String)
                {
                    filePaths.Clear();
                    return false;
                }

                string? filePath = filePathElement.GetString();
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    filePaths.Clear();
                    return false;
                }

                filePaths.Add(filePath.Trim());
            }

            return filePaths.Count > 0;
        }
        catch (JsonException)
        {
            filePaths.Clear();
            return false;
        }
    }
}
