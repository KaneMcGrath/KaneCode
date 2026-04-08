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
    private static readonly Regex ToolCallBlockRegex = new(
        "<tool_call>\\s*(?<body>.*?)\\s*</tool_call>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex FunctionBlockRegex = new(
        "<function=(?<name>[A-Za-z_][A-Za-z0-9_]*)>\\s*(?<body>.*?)\\s*</function>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ParameterBlockRegex = new(
        "<parameter=(?<name>[A-Za-z_][A-Za-z0-9_]*)>\\s*(?<value>.*?)\\s*</parameter>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

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

        MatchCollection matches = ToolCallBlockRegex.Matches(text);
        foreach (Match match in matches)
        {
            if (!match.Success)
            {
                continue;
            }

            string rawText = match.Value.Trim();
            string body = match.Groups["body"].Value;

            bool parseSucceeded = TryParseToolCall(body, out string? functionName, out string argumentsJson, out string error);
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

/// <summary>
/// Represents a malformed tool call recovered from assistant text.
/// </summary>
internal sealed record RecoveredMalformedToolCall(
    int Index,
    string FunctionName,
    string ArgumentsJson,
    string Error,
    string RawText);
