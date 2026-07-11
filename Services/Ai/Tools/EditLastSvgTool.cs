using System.IO;
using System.Text.Json;
using Svg;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that applies search-and-replace edits to the most recent SVG
/// drawn by <see cref="DrawSvgTool"/>. Supports multiple edits in a single call
/// to avoid agents having to repeat long SVG content when making small changes.
/// The updated SVG is re-rendered inline after edits are applied.
/// </summary>
internal sealed class EditLastSvgTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "edits": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "oldText": {
                                "type": "string",
                                "description": "The exact text to find in the last SVG. Must match exactly one location."
                            },
                            "newText": {
                                "type": "string",
                                "description": "The replacement text."
                            }
                        },
                        "required": ["oldText", "newText"]
                    },
                    "description": "An array of search-and-replace edits to apply to the last SVG. Edits are applied in order. Batch multiple edits together to avoid repeating the full SVG content."
                }
            },
            "required": ["edits"]
        }
        """).RootElement.Clone();

    public EditLastSvgTool()
    {
    }

    public string Name => "edit_last_svg";

    public string Category => "Drawing";

    public string Description =>
        "Applies one or more search-and-replace edits to the last SVG drawn by draw_svg. " +
        "Use this to make incremental changes without repeating the full SVG content. " +
        "Each edit specifies oldText (exact text to find) and newText (replacement). " +
        "Edits are applied in order. Batch multiple edits in a single call whenever possible. " +
        "The updated SVG is re-rendered inline in the chat.";

    public JsonElement ParametersSchema => Schema;

    public bool RequiresConfirmation => false;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string? currentSvg = DrawSvgTool.LastSvgContent;

        if (string.IsNullOrWhiteSpace(currentSvg))
        {
            return Task.FromResult(ToolCallResult.Fail(
                "No SVG has been drawn yet. Use draw_svg first to create an SVG, then use edit_last_svg to make changes."));
        }

        if (!arguments.TryGetProperty("edits", out var editsElement) ||
            editsElement.ValueKind != JsonValueKind.Array)
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: edits (must be an array)"));
        }

        int editCount = editsElement.GetArrayLength();
        if (editCount == 0)
        {
            return Task.FromResult(ToolCallResult.Fail("edits array must not be empty."));
        }

        string updatedSvg = currentSvg;
        List<int> appliedLineNumbers = [];
        int editsApplied = 0;

        foreach (JsonElement edit in editsElement.EnumerateArray())
        {
            if (!edit.TryGetProperty("oldText", out var oldTextElement))
            {
                return Task.FromResult(ToolCallResult.Fail(
                    $"Edit {editsApplied + 1}: missing required parameter: oldText"));
            }

            if (!edit.TryGetProperty("newText", out var newTextElement))
            {
                return Task.FromResult(ToolCallResult.Fail(
                    $"Edit {editsApplied + 1}: missing required parameter: newText"));
            }

            string oldText = oldTextElement.GetString() ?? string.Empty;
            string newText = newTextElement.GetString() ?? string.Empty;

            if (oldText.Length == 0)
            {
                return Task.FromResult(ToolCallResult.Fail(
                    $"Edit {editsApplied + 1}: oldText must not be empty"));
            }

            int matchCount = CountOccurrences(updatedSvg, oldText);

            if (matchCount == 0)
            {
                return Task.FromResult(ToolCallResult.Fail(
                    $"Edit {editsApplied + 1}: oldText not found in the SVG. " +
                    "Ensure the text matches exactly, including whitespace."));
            }

            if (matchCount > 1)
            {
                return Task.FromResult(ToolCallResult.Fail(
                    $"Edit {editsApplied + 1}: oldText matches {matchCount} locations in the SVG. " +
                    "Provide more surrounding context to make it unique."));
            }

            int matchIndex = updatedSvg.IndexOf(oldText, StringComparison.Ordinal);
            int lineNumber = GetLineNumber(updatedSvg, matchIndex);

            updatedSvg = updatedSvg.Remove(matchIndex, oldText.Length)
                .Insert(matchIndex, newText);

            appliedLineNumbers.Add(lineNumber);
            editsApplied++;
        }

        // Validate that the updated SVG is still well-formed
        try
        {
            using var svgStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(updatedSvg));
            SvgDocument.Open<SvgDocument>(svgStream);
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolCallResult.Fail(
                $"Edits produced invalid SVG: {ex.Message}. The last valid SVG is preserved."));
        }

        DrawSvgTool.LastSvgContent = updatedSvg;

        string linesDesc = editsApplied == 1
            ? $"line {appliedLineNumbers[0]}"
            : $"lines {string.Join(", ", appliedLineNumbers)}";

        return Task.FromResult(ToolCallResult.OkWithSvg(
            $"{editsApplied} edit(s) applied at {linesDesc}.", updatedSvg));
    }

    private static int CountOccurrences(string source, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }

    private static int GetLineNumber(string content, int charIndex)
    {
        var line = 1;
        for (var i = 0; i < charIndex && i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}
