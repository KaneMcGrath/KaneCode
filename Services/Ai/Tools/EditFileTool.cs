using System.IO;
using System.Text.Json;

namespace KaneCode.Services.Ai.Tools;

/// <summary>
/// Agent tool that applies a single search-and-replace edit within a file.
/// Fails if <c>oldText</c> is not found, or matches more than one location.
///
/// The tool exposes configurable backend options (see <see cref="BackendOptionsSchema"/>)
/// so presets can choose which edit engine executes and tune matching behavior:
/// <list type="bullet">
/// <item><b>exact_match</b> — exact replace with an optional indentation-insensitive fallback (current behavior).</item>
/// <item><b>unified_diff</b> — hunk-style matching that tolerates up to <c>context_lines</c> differing lines.</item>
/// <item><b>anchored_replace</b> — matches a block by its first and last significant lines; robust to indentation drift.</item>
/// </list>
/// </summary>
internal sealed class EditFileTool : IAgentTool
{
    private readonly record struct TextMatch(int StartIndex, int Length);

    private readonly record struct ScoredMatch(TextMatch Match, int Score);

    private readonly record struct LineSegment(int StartIndex, int Length, bool HasTrailingNewline);

    private sealed record EditOptions(
        string Engine,
        int ContextLines,
        bool CaseSensitive,
        bool IndentationInsensitiveFallback,
        string OnMultipleMatches,
        string PathScope,
        int TimeoutSeconds);

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

    private static readonly JsonElement BackendOptions = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "engine": {
                    "type": "string",
                    "enum": ["exact_match", "unified_diff", "anchored_replace"],
                    "default": "exact_match",
                    "description": "Which engine executes this tool for this preset",
                    "x-enum-descriptions": {
                        "unified_diff": "Token-efficient hunks with context — best default.",
                        "anchored_replace": "Robust to indentation drift; replaces anchored blocks.",
                        "exact_match": "Current behavior — exact replace + indentation fallback."
                    },
                    "x-enum-recommended": ["unified_diff"]
                },
                "context_lines": {
                    "type": "integer",
                    "default": 3,
                    "minimum": 0,
                    "maximum": 10,
                    "description": "Context lines to tolerate when matching a hunk",
                    "engines": ["unified_diff"]
                },
                "case_sensitive": {
                    "type": "boolean",
                    "default": false,
                    "description": "Case-sensitive matching",
                    "engines": ["exact_match", "unified_diff", "anchored_replace"]
                },
                "indentation_insensitive_fallback": {
                    "type": "boolean",
                    "default": true,
                    "description": "Fall back to indentation-insensitive matching when no exact match is found",
                    "engines": ["exact_match", "unified_diff"]
                },
                "on_multiple_matches": {
                    "type": "string",
                    "enum": ["fail", "most_context", "first_only"],
                    "default": "fail",
                    "description": "Behavior when oldText matches multiple locations",
                    "engines": ["exact_match", "unified_diff", "anchored_replace"]
                },
                "require_confirmation": {
                    "type": "boolean",
                    "default": true,
                    "description": "Require user confirmation before applying the edit"
                },
                "max_retries": {
                    "type": "integer",
                    "default": 2,
                    "minimum": 0,
                    "maximum": 10,
                    "description": "Maximum number of retries on transient failures"
                },
                "timeout": {
                    "type": "integer",
                    "default": 30,
                    "minimum": 1,
                    "maximum": 600,
                    "description": "Execution timeout in seconds"
                },
                "path_scope": {
                    "type": "string",
                    "enum": ["project", "project_external"],
                    "default": "project",
                    "description": "Where file paths may resolve: project only, or project plus attached external context folders"
                },
                "log_verbosity": {
                    "type": "string",
                    "enum": ["quiet", "normal", "verbose"],
                    "default": "normal",
                    "description": "How much detail to log while executing"
                }
            }
        }
        """).RootElement.Clone();

    private static readonly JsonElement DefaultOptions = JsonDocument.Parse("""
        {
            "engine": "exact_match",
            "context_lines": 3,
            "case_sensitive": false,
            "indentation_insensitive_fallback": true,
            "on_multiple_matches": "fail",
            "require_confirmation": true,
            "max_retries": 2,
            "timeout": 30,
            "path_scope": "project",
            "log_verbosity": "normal"
        }
        """).RootElement.Clone();

    private readonly Func<string?> _projectRootProvider;
    private readonly Action<string>? _onFileChanged;
    private readonly ExternalContextDirectoryRegistry? _externalContextDirectoryRegistry;

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

    public EditFileTool(
        Func<string?> projectRootProvider,
        Action<string>? onFileChanged = null,
        ExternalContextDirectoryRegistry? externalContextDirectoryRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(projectRootProvider);
        _projectRootProvider = projectRootProvider;
        _onFileChanged = onFileChanged;
        _externalContextDirectoryRegistry = externalContextDirectoryRegistry;
    }

    public string Name => "edit";

    public string Category => "Write Files";

    public string Description => "Apply a single search-and-replace edit within a file. Fails if oldText is not found or matches multiple locations.";

    public JsonElement ParametersSchema => Schema;

    public JsonElement BackendOptionsSchema => BackendOptions;

    public IReadOnlyDictionary<string, JsonElement> DefaultBackendOptions
    {
        get
        {
            Dictionary<string, JsonElement> defaults = new(StringComparer.Ordinal);
            foreach (JsonProperty property in DefaultOptions.EnumerateObject())
            {
                defaults[property.Name] = property.Value.Clone();
            }

            return defaults;
        }
    }

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

        EditOptions options = ReadOptions();

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
            resolvedPath = options.PathScope == "project_external" && _externalContextDirectoryRegistry is not null
                ? AgentToolPathResolver.ResolvePath(
                    _projectRootProvider,
                    filePath,
                    _externalContextDirectoryRegistry.GetAllowedDirectories())
                : AgentToolPathResolver.ResolvePath(_projectRootProvider, filePath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(ToolCallResult.Fail(ex.Message));
        }

        // Apply the configured execution timeout (advisory for this fast, synchronous tool).
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.TimeoutSeconds > 0)
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        }

        if (timeoutCts.IsCancellationRequested)
        {
            return Task.FromResult(ToolCallResult.Fail("Tool execution timed out."));
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

        TextMatch match;
        string? matchError = null;
        if (!TryFindMatch(normalizedContent, oldText, options, out match, out matchError))
        {
            return Task.FromResult(ToolCallResult.Fail(matchError));
        }

        // Perform the replacement on normalized content
        string updatedNormalizedContent = normalizedContent.Remove(match.StartIndex, match.Length)
            .Insert(match.StartIndex, newText);

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
        int lineNumber = GetLineNumber(normalizedContent, match.StartIndex);
        return Task.FromResult(ToolCallResult.Ok(
            $"Edit applied at line {lineNumber} in '{resolvedPath}'."));
    }

    /// <summary>
    /// Reads the effective backend options from <see cref="AgentToolContext"/>,
    /// falling back to the tool defaults when no context is pushed.
    /// </summary>
    private static EditOptions ReadOptions()
    {
        return new EditOptions(
            Engine: AgentToolContext.GetString("engine", "exact_match"),
            ContextLines: Math.Clamp(AgentToolContext.GetInt("context_lines", 3), 0, 10),
            CaseSensitive: AgentToolContext.GetBool("case_sensitive", false),
            IndentationInsensitiveFallback: AgentToolContext.GetBool("indentation_insensitive_fallback", true),
            OnMultipleMatches: AgentToolContext.GetString("on_multiple_matches", "fail"),
            PathScope: AgentToolContext.GetString("path_scope", "project"),
            TimeoutSeconds: Math.Clamp(AgentToolContext.GetInt("timeout", 30), 1, 600));
    }

    /// <summary>
    /// Finds the single best match for <paramref name="oldText"/> in
    /// <paramref name="content"/> using the configured engine and options.
    /// Returns false and sets <paramref name="error"/> when no unique match exists.
    /// </summary>
    private static bool TryFindMatch(
        string content,
        string oldText,
        EditOptions options,
        out TextMatch match,
        out string? error)
    {
        match = default;
        error = null;

        StringComparison comparison = options.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        switch (options.Engine)
        {
            case "unified_diff":
                return TryResolveUnifiedMatches(content, oldText, options, comparison, out match, out error);

            case "anchored_replace":
                IReadOnlyList<TextMatch> anchored = FindAnchoredMatches(content, oldText, comparison);
                return ResolveCandidates(
                    anchored.Select(m => (Match: m, Score: 0)).ToList(),
                    options.OnMultipleMatches,
                    oldText,
                    out match,
                    out error);

            default: // exact_match
                int exactCount = CountOccurrences(content, oldText, comparison);

                if (exactCount == 1)
                {
                    int exactIndex = content.IndexOf(oldText, comparison);
                    match = new TextMatch(exactIndex, oldText.Length);
                    return true;
                }

                if (exactCount > 1)
                {
                    return ResolveCandidates(
                        Enumerable.Range(0, exactCount)
                            .Select(i => (Match: new TextMatch(IndexOfNth(content, oldText, comparison, i), oldText.Length), Score: 0))
                            .ToList(),
                        options.OnMultipleMatches,
                        oldText,
                        out match,
                        out error);
                }

                if (options.IndentationInsensitiveFallback)
                {
                    IReadOnlyList<TextMatch> fallback = FindIndentationInsensitiveMatches(content, oldText, comparison);
                    return ResolveCandidates(
                        fallback.Select(m => (Match: m, Score: 0)).ToList(),
                        options.OnMultipleMatches,
                        oldText,
                        out match,
                        out error);
                }

                error = $"oldText not found in file. Ensure the text matches exactly, including whitespace and line endings.";
                return false;
        }
    }

    private static bool TryResolveUnifiedMatches(
        string content,
        string oldText,
        EditOptions options,
        StringComparison comparison,
        out TextMatch match,
        out string? error)
    {
        List<(TextMatch Match, int Score)> candidates = [];

        List<LineSegment> contentLines = GetLineSegments(content);
        List<LineSegment> oldTextLines = GetLineSegments(oldText);

        if (oldTextLines.Count == 0 || oldTextLines.Count > contentLines.Count)
        {
            error = "oldText not found in file.";
            match = default;
            return false;
        }

        int threshold = Math.Max(1, oldTextLines.Count - options.ContextLines);

        for (int contentLineIndex = 0; contentLineIndex <= contentLines.Count - oldTextLines.Count; contentLineIndex++)
        {
            int score = 0;
            bool firstLineMatches = false;

            for (int oldLineIndex = 0; oldLineIndex < oldTextLines.Count; oldLineIndex++)
            {
                string contentLineText = LineText(content, contentLines[contentLineIndex + oldLineIndex]);
                string oldLineText = LineText(oldText, oldTextLines[oldLineIndex]);

                bool matches = string.Equals(contentLineText, oldLineText, comparison)
                    || (options.IndentationInsensitiveFallback &&
                        string.Equals(TrimLeadingIndentation(contentLineText), TrimLeadingIndentation(oldLineText), comparison));

                if (matches)
                {
                    score++;
                    if (oldLineIndex == 0)
                    {
                        firstLineMatches = true;
                    }
                }
            }

            if (score >= threshold && firstLineMatches)
            {
                LineSegment firstLine = contentLines[contentLineIndex];
                LineSegment lastLine = contentLines[contentLineIndex + oldTextLines.Count - 1];
                bool oldTextEndsWithNewline = oldTextLines[oldTextLines.Count - 1].HasTrailingNewline;
                int startIndex = firstLine.StartIndex;
                int endIndex = lastLine.StartIndex + lastLine.Length +
                    (oldTextEndsWithNewline && lastLine.HasTrailingNewline ? 1 : 0);
                candidates.Add((new TextMatch(startIndex, endIndex - startIndex), score));
            }
        }

        return ResolveCandidates(candidates, options.OnMultipleMatches, oldText, out match, out error);
    }

    private static bool ResolveCandidates(
        IReadOnlyList<(TextMatch Match, int Score)> candidates,
        string onMultipleMatches,
        string oldText,
        out TextMatch match,
        out string? error)
    {
        match = default;
        error = null;

        if (candidates.Count == 0)
        {
            error = "oldText not found in file. Ensure the text matches exactly, including whitespace and line endings.";
            return false;
        }

        if (candidates.Count == 1)
        {
            match = candidates[0].Match;
            return true;
        }

        switch (onMultipleMatches)
        {
            case "first_only":
                match = candidates[0].Match;
                return true;

            case "most_context":
                int bestScore = candidates.Max(c => c.Score);
                List<(TextMatch Match, int Score)> best = candidates.Where(c => c.Score == bestScore).ToList();
                if (best.Count == 1)
                {
                    match = best[0].Match;
                    return true;
                }

                error = $"oldText matches {candidates.Count} locations with equal context. Provide more surrounding context to make it unique.";
                return false;

            default: // "fail"
                error = $"oldText matches {candidates.Count} locations. Provide more surrounding context to make it unique.";
                return false;
        }
    }

    private static IReadOnlyList<TextMatch> FindIndentationInsensitiveMatches(
        string content,
        string oldText,
        StringComparison comparison)
    {
        List<LineSegment> contentLines = GetLineSegments(content);
        List<LineSegment> oldTextLines = GetLineSegments(oldText);
        List<TextMatch> matches = [];

        if (oldTextLines.Count == 0 || oldTextLines.Count > contentLines.Count)
        {
            return matches;
        }

        for (int contentLineIndex = 0; contentLineIndex <= contentLines.Count - oldTextLines.Count; contentLineIndex++)
        {
            bool isMatch = true;

            for (int oldTextLineIndex = 0; oldTextLineIndex < oldTextLines.Count; oldTextLineIndex++)
            {
                LineSegment contentLine = contentLines[contentLineIndex + oldTextLineIndex];
                LineSegment oldTextLine = oldTextLines[oldTextLineIndex];
                string contentLineText = content.Substring(contentLine.StartIndex, contentLine.Length);
                string oldTextLineText = oldText.Substring(oldTextLine.StartIndex, oldTextLine.Length);

                if (contentLine.HasTrailingNewline != oldTextLine.HasTrailingNewline ||
                    !string.Equals(TrimLeadingIndentation(contentLineText), TrimLeadingIndentation(oldTextLineText), comparison))
                {
                    isMatch = false;
                    break;
                }
            }

            if (!isMatch)
            {
                continue;
            }

            LineSegment firstLine = contentLines[contentLineIndex];
            LineSegment lastLine = contentLines[contentLineIndex + oldTextLines.Count - 1];
            int matchStartIndex = firstLine.StartIndex;
            int matchEndIndex = lastLine.StartIndex + lastLine.Length + (lastLine.HasTrailingNewline ? 1 : 0);
            matches.Add(new TextMatch(matchStartIndex, matchEndIndex - matchStartIndex));
        }

        return matches;
    }

    /// <summary>
    /// Anchored matching: locates a block by its first and last significant
    /// (non-blank) lines. Robust to indentation drift and interior differences.
    /// </summary>
    private static IReadOnlyList<TextMatch> FindAnchoredMatches(
        string content,
        string oldText,
        StringComparison comparison)
    {
        List<LineSegment> contentLines = GetLineSegments(content);
        List<LineSegment> oldTextLines = GetLineSegments(oldText);
        List<TextMatch> matches = [];

        if (oldTextLines.Count == 0 || oldTextLines.Count > contentLines.Count)
        {
            return matches;
        }

        int firstSignificant = FirstNonBlankLineIndex(oldTextLines);
        int lastSignificant = LastNonBlankLineIndex(oldTextLines);

        if (firstSignificant < 0 || lastSignificant < firstSignificant)
        {
            return matches;
        }

        int anchorSpan = lastSignificant - firstSignificant;

        for (int contentLineIndex = 0; contentLineIndex + anchorSpan < contentLines.Count; contentLineIndex++)
        {
            string firstContentLine = LineText(content, contentLines[contentLineIndex]);
            string firstOldLine = LineText(oldText, oldTextLines[firstSignificant]);

            if (!string.Equals(TrimLeadingIndentation(firstContentLine), TrimLeadingIndentation(firstOldLine), comparison))
            {
                continue;
            }

            int lastContentIndex = contentLineIndex + anchorSpan;
            string lastContentLine = LineText(content, contentLines[lastContentIndex]);
            string lastOldLine = LineText(oldText, oldTextLines[lastSignificant]);

            if (!string.Equals(TrimLeadingIndentation(lastContentLine), TrimLeadingIndentation(lastOldLine), comparison))
            {
                continue;
            }

            LineSegment firstLine = contentLines[contentLineIndex];
            LineSegment lastLine = contentLines[lastContentIndex];
            bool oldTextEndsWithNewline = oldTextLines[lastSignificant].HasTrailingNewline;
            int startIndex = firstLine.StartIndex;
            int endIndex = lastLine.StartIndex + lastLine.Length +
                (oldTextEndsWithNewline && lastLine.HasTrailingNewline ? 1 : 0);
            matches.Add(new TextMatch(startIndex, endIndex - startIndex));
        }

        return matches;
    }

    private static int FirstNonBlankLineIndex(List<LineSegment> lines)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length > 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static int LastNonBlankLineIndex(List<LineSegment> lines)
    {
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].Length > 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static string LineText(string content, LineSegment segment)
    {
        return content.Substring(segment.StartIndex, segment.Length);
    }

    private static List<LineSegment> GetLineSegments(string content)
    {
        List<LineSegment> segments = [];
        int lineStartIndex = 0;

        for (int index = 0; index < content.Length; index++)
        {
            if (content[index] != '\n')
            {
                continue;
            }

            segments.Add(new LineSegment(lineStartIndex, index - lineStartIndex, true));
            lineStartIndex = index + 1;
        }

        if (lineStartIndex < content.Length || content.Length == 0)
        {
            segments.Add(new LineSegment(lineStartIndex, content.Length - lineStartIndex, false));
        }

        return segments;
    }

    private static string TrimLeadingIndentation(string line)
    {
        int index = 0;

        while (index < line.Length && (line[index] == ' ' || line[index] == '\t'))
        {
            index++;
        }

        return line[index..];
    }

    private static int CountOccurrences(string source, string search, StringComparison comparison)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(search, index, comparison)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }

    private static int IndexOfNth(string source, string search, StringComparison comparison, int occurrence)
    {
        var index = -1;
        for (var i = 0; i <= occurrence; i++)
        {
            index = source.IndexOf(search, index + 1, comparison);
        }

        return index;
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
