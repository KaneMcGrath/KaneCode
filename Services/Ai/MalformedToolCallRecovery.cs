using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KaneCode.Services.Ai;

/// <summary>
/// Detects tool-call markup that was emitted as plain assistant text instead of a real tool invocation.
/// </summary>
internal static class MalformedToolCallRecovery
{
    private const string ToolCallStartTag = "<tool_call>";

    private const string ToolCallEndTag = "</tool_call>";

    private static readonly Regex FunctionStartRegex = new(
        "<function=(?<name>[A-Za-z_][A-Za-z0-9_]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FunctionBlockRegex = new(
        "<function=(?<name>[A-Za-z_][A-Za-z0-9_]*)>\\s*(?<body>.*?)\\s*</function>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ParameterBlockRegex = new(
        "<parameter=(?<name>[A-Za-z_][A-Za-z0-9_]*)>\\s*(?<value>.*?)\\s*</parameter>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public static IReadOnlyList<StreamingMalformedToolCall> DetectStreaming(string? reasoningContent, string? responseContent)
    {
        List<StreamingMalformedToolCall> detectedToolCalls = [];
        AddStreamingToolCalls(detectedToolCalls, reasoningContent);
        AddStreamingToolCalls(detectedToolCalls, responseContent);
        return detectedToolCalls;
    }

    public static IReadOnlyList<RecoveredMalformedToolCall> Recover(string? reasoningContent, string? responseContent)
    {
        List<RecoveredMalformedToolCall> recoveredToolCalls = [];
        AddRecoveredToolCalls(recoveredToolCalls, reasoningContent, "reasoning");
        AddRecoveredToolCalls(recoveredToolCalls, responseContent, "response");
        return recoveredToolCalls;
    }

    private static void AddRecoveredToolCalls(List<RecoveredMalformedToolCall> recoveredToolCalls, string? text, string source)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (ToolCallMarkupSegment segment in EnumerateToolCallMarkup(text))
        {
            string rawText = segment.RawText;
            string? functionName = TryGetFunctionName(segment.Body);
            string argumentsJson = "{}";
            string error;
            bool parseSucceeded;

            if (segment.IsComplete)
            {
                parseSucceeded = TryParseToolCall(segment.Body, out string? parsedFunctionName, out argumentsJson, out error);
                if (!string.IsNullOrWhiteSpace(parsedFunctionName))
                {
                    functionName = parsedFunctionName;
                }
            }
            else
            {
                parseSucceeded = false;
                error = "The tool-call markup did not contain a closing </tool_call> tag.";
            }

            string resolvedFunctionName = string.IsNullOrWhiteSpace(functionName)
                ? "invalid_tool_call"
                : functionName;

            string recoveryError = parseSucceeded
                ? $"Malformed tool call detected in assistant {source}. The model wrote tool markup instead of invoking the tool API. Retry the same call as a real tool invocation. Raw tool text: {rawText}"
                : $"Malformed tool call detected in assistant {source}. {error} Retry using a real tool invocation. Raw tool text: {rawText}";

            recoveredToolCalls.Add(new RecoveredMalformedToolCall(
                recoveredToolCalls.Count,
                resolvedFunctionName,
                argumentsJson,
                recoveryError,
                rawText));
        }
    }

    private static void AddStreamingToolCalls(List<StreamingMalformedToolCall> detectedToolCalls, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (ToolCallMarkupSegment segment in EnumerateToolCallMarkup(text))
        {
            string functionName = TryGetFunctionName(segment.Body) ?? "tool_call";
            detectedToolCalls.Add(new StreamingMalformedToolCall(
                detectedToolCalls.Count,
                functionName,
                segment.RawText,
                segment.IsComplete));
        }
    }

    private static IEnumerable<ToolCallMarkupSegment> EnumerateToolCallMarkup(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        int searchStart = 0;
        while (searchStart < text.Length)
        {
            int toolCallStart = text.IndexOf(ToolCallStartTag, searchStart, StringComparison.OrdinalIgnoreCase);
            if (toolCallStart < 0)
            {
                yield break;
            }

            int nextToolCallStart = text.IndexOf(ToolCallStartTag, toolCallStart + ToolCallStartTag.Length, StringComparison.OrdinalIgnoreCase);
            int toolCallEnd = text.IndexOf(ToolCallEndTag, toolCallStart + ToolCallStartTag.Length, StringComparison.OrdinalIgnoreCase);
            bool isComplete = toolCallEnd >= 0 && (nextToolCallStart < 0 || toolCallEnd < nextToolCallStart);

            int rawTextEnd = isComplete
                ? toolCallEnd + ToolCallEndTag.Length
                : nextToolCallStart >= 0
                    ? nextToolCallStart
                    : text.Length;

            int bodyStart = toolCallStart + ToolCallStartTag.Length;
            int bodyEnd = isComplete ? toolCallEnd : rawTextEnd;
            string rawText = text[toolCallStart..rawTextEnd].Trim();
            string body = text[bodyStart..bodyEnd];

            yield return new ToolCallMarkupSegment(rawText, body, isComplete);
            searchStart = rawTextEnd;
        }
    }

    private static string? TryGetFunctionName(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        Match functionMatch = FunctionStartRegex.Match(body);
        return functionMatch.Success
            ? functionMatch.Groups["name"].Value
            : null;
    }

    private static bool TryParseToolCall(string body, out string? functionName, out string argumentsJson, out string error)
    {
        functionName = null;
        argumentsJson = "{}";
        error = "The tool-call markup could not be parsed.";

        Match functionMatch = FunctionBlockRegex.Match(body);
        if (!functionMatch.Success)
        {
            error = "The tool-call markup did not contain a complete function block.";
            return false;
        }

        string functionBlock = functionMatch.Value;
        string leadingOrTrailingContent = body.Replace(functionBlock, string.Empty, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(leadingOrTrailingContent))
        {
            functionName = functionMatch.Groups["name"].Value;
            error = "The tool-call markup contained unexpected content outside the function block.";
            return false;
        }

        functionName = functionMatch.Groups["name"].Value;
        string functionBody = functionMatch.Groups["body"].Value;
        MatchCollection parameterMatches = ParameterBlockRegex.Matches(functionBody);

        string remainingContent = functionBody;
        Dictionary<string, string> parameters = new(StringComparer.Ordinal);
        foreach (Match parameterMatch in parameterMatches)
        {
            if (!parameterMatch.Success)
            {
                continue;
            }

            string parameterName = parameterMatch.Groups["name"].Value;
            string parameterValue = parameterMatch.Groups["value"].Value.Trim();
            parameters[parameterName] = parameterValue;
            remainingContent = remainingContent.Replace(parameterMatch.Value, string.Empty, StringComparison.Ordinal);
        }

        if (!string.IsNullOrWhiteSpace(remainingContent))
        {
            error = "The function block contained unexpected content outside parameter blocks.";
            return false;
        }

        argumentsJson = BuildArgumentsJson(parameters);
        error = string.Empty;
        return true;
    }

    private static string BuildArgumentsJson(IReadOnlyDictionary<string, string> parameters)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();

            foreach (KeyValuePair<string, string> parameter in parameters)
            {
                writer.WriteString(parameter.Key, parameter.Value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

internal sealed record StreamingMalformedToolCall(
    int Index,
    string FunctionName,
    string RawText,
    bool IsComplete);

internal sealed record ToolCallMarkupSegment(
    string RawText,
    string Body,
    bool IsComplete);

/// <summary>
/// Represents a malformed tool call recovered from assistant text.
/// </summary>
internal sealed record RecoveredMalformedToolCall(
    int Index,
    string FunctionName,
    string ArgumentsJson,
    string Error,
    string RawText);
