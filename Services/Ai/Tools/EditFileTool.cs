using System.IO;
using System.Text.Json;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that applies a single search-and-replace edit within a file.
/// Fails if <c>oldText</c> is not found, or matches more than one location.
/// </summary>
internal sealed class EditFileTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "filePath": {
                    "type": "string",
                    "description": "The path to the file to edit. Can be absolute or relative to the loaded project root, but must stay inside the loaded project."
                },
                "oldText": {
                    "type": "string",
                    "description": "The exact text to find in the file. Must match exactly one location."
                },
                "newText": {
                    "type": "string",
                    "description": "The replacement text."
                }
            },
            "required": ["filePath", "oldText", "newText"]
        }
        """).RootElement.Clone();

    private readonly Func<string?> _projectRootProvider;
    private readonly Action<string>? _onFileChanged;

    /// <summary>
    /// Normalizes line endings by converting CRLF to LF.
    /// This ensures consistent internal representation regardless of platform.
    /// </summary>
    private static string NormalizeLineEndings(string content)
    {
        return content.Replace("\r\n", "\n").Replace("\r", "\n");
    }

    /// <summary>
    /// Converts normalized LF line endings to platform-specific line endings.
    /// On Windows, converts \n to \r\n. On other platforms, keeps \n.
    /// </summary>
    private static string ConvertToPlatformLineEndings(string normalizedContent)
    {
        var isWindows = Path.DirectorySeparatorChar == '\\';
        return isWindows ? normalizedContent.Replace("\n", "\r\n") : normalizedContent;
    }

    public EditFileTool(Func<string?> projectRootProvider, Action<string>? onFileChanged = null)
    {
        ArgumentNullException.ThrowIfNull(projectRootProvider);
        _projectRootProvider = projectRootProvider;
        _onFileChanged = onFileChanged;
    }

    public string Name => "edit_file";

    public string Description => "Apply a single search-and-replace edit within a file. Fails if oldText is not found or matches multiple locations.";

    public JsonElement ParametersSchema => Schema;

    public bool RequiresConfirmation => true;

    public Task<ToolCallResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("filePath", out var filePathElement) ||
            string.IsNullOrWhiteSpace(filePathElement.GetString()))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: filePath"));
        }

        if (!arguments.TryGetProperty("oldText", out var oldTextElement))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: oldText"));
        }

        if (!arguments.TryGetProperty("newText", out var newTextElement))
        {
            return Task.FromResult(ToolCallResult.Fail("Missing required parameter: newText"));
        }

        string filePath = filePathElement.GetString()!.Trim();
        string oldText = oldTextElement.GetString() ?? string.Empty;
        string newText = newTextElement.GetString() ?? string.Empty;

        // Normalize line endings for both oldText and newText
        oldText = NormalizeLineEndings(oldText);
        newText = NormalizeLineEndings(newText);

        if (oldText.Length == 0)
        {
            return Task.FromResult(ToolCallResult.Fail("oldText must not be empty"));
        }

        string resolvedPath;

        try
        {
            resolvedPath = AgentToolPathResolver.ResolvePath(_projectRootProvider, filePath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(ToolCallResult.Fail(ex.Message));
        }

        if (!File.Exists(resolvedPath))
        {
            return Task.FromResult(ToolCallResult.Fail($"File not found: {filePath}"));
        }

        string originalContent;
        try
        {
            originalContent = File.ReadAllText(resolvedPath);
        }
        catch (IOException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"IO error reading file: {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"Access denied: {ex.Message}"));
        }

        // Normalize the file content to LF for consistent matching
        string normalizedContent = NormalizeLineEndings(originalContent);

        int matchCount = CountOccurrences(normalizedContent, oldText);

        if (matchCount == 0)
        {
            return Task.FromResult(ToolCallResult.Fail(
                $"oldText not found in '{filePath}'. Ensure the text matches exactly, including whitespace and line endings."));
        }

        if (matchCount > 1)
        {
            return Task.FromResult(ToolCallResult.Fail(
                $"oldText matches {matchCount} locations in '{filePath}'. Provide more surrounding context to make it unique."));
        }

        // Perform the replacement on normalized content
        string updatedNormalizedContent = normalizedContent.Replace(oldText, newText, StringComparison.Ordinal);

        // Convert back to platform-specific line endings before writing
        string finalContent = ConvertToPlatformLineEndings(updatedNormalizedContent);

        try
        {
            File.WriteAllText(resolvedPath, finalContent);
            _onFileChanged?.Invoke(resolvedPath);
        }
        catch (IOException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"IO error writing file: {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(ToolCallResult.Fail($"Access denied: {ex.Message}"));
        }

        // Calculate line number in the normalized content
        int lineNumber = GetLineNumber(normalizedContent, normalizedContent.IndexOf(oldText, StringComparison.Ordinal));
        return Task.FromResult(ToolCallResult.Ok(
            $"Edit applied at line {lineNumber} in '{resolvedPath}'."));
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