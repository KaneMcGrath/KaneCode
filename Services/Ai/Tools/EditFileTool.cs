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
                    "description": "The path to the file to edit. Can be absolute or relative to the project root."
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

        var filePath = filePathElement.GetString()!.Trim();
        var oldText = oldTextElement.GetString() ?? string.Empty;
        var newText = newTextElement.GetString() ?? string.Empty;

        if (oldText.Length == 0)
        {
            return Task.FromResult(ToolCallResult.Fail("oldText must not be empty"));
        }

        var resolvedPath = ResolvePath(filePath);

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

        var matchCount = CountOccurrences(originalContent, oldText);

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

        var updatedContent = originalContent.Replace(oldText, newText, StringComparison.Ordinal);

        try
        {
            File.WriteAllText(resolvedPath, updatedContent);
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

        var lineNumber = GetLineNumber(originalContent, originalContent.IndexOf(oldText, StringComparison.Ordinal));
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

    private string ResolvePath(string filePath)
    {
        if (Path.IsPathRooted(filePath))
        {
            return filePath;
        }

        var root = _projectRootProvider();
        if (string.IsNullOrWhiteSpace(root))
        {
            return filePath;
        }

        // Root may point to a .sln or .csproj file — use its directory
        if (File.Exists(root))
        {
            root = Path.GetDirectoryName(root);
        }

        return string.IsNullOrWhiteSpace(root)
            ? filePath
            : Path.GetFullPath(Path.Combine(root, filePath));
    }
}
