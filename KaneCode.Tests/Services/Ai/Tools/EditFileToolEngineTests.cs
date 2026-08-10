using KaneCode.Services.Ai;
using KaneCode.Services.Ai.Tools;
using System.IO;
using System.Text.Json;

namespace KaneCode.Tests.Services.Ai.Tools;

public sealed class EditFileToolEngineTests : IDisposable
{
    private readonly string _tempDir;
    private readonly EditFileTool _tool;

    public EditFileToolEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"KaneCodeEditEngine_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tool = new EditFileTool(() => _tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    private async Task<ToolCallResult> ExecuteEditAsync(string fileName, string oldText, string newText, Dictionary<string, JsonElement>? options = null)
    {
        string path = Path.Combine(_tempDir, fileName);
        JsonElement args = JsonDocument.Parse(BuildArgs(fileName, oldText, newText)).RootElement;

        if (options is null)
        {
            return await _tool.ExecuteAsync(args);
        }

        using (AgentToolContext.Push(options))
        {
            return await _tool.ExecuteAsync(args);
        }
    }

    private static string BuildArgs(string fileName, string oldText, string newText)
    {
        string Escape(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal)
                                    .Replace("\"", "\\\"", StringComparison.Ordinal)
                                    .Replace("\r", "\\r", StringComparison.Ordinal)
                                    .Replace("\n", "\\n", StringComparison.Ordinal)
                                    .Replace("\t", "\\t", StringComparison.Ordinal);
        return $$"""
            {
              "filePath": "{{Escape(fileName)}}",
              "oldText": "{{Escape(oldText)}}",
              "newText": "{{Escape(newText)}}"
            }
            """;
    }

    private static async Task<string> ReadNormalizedAsync(string path)
    {
        return (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static Dictionary<string, JsonElement> Options(params (string Key, object Value)[] values)
    {
        Dictionary<string, JsonElement> dict = new(StringComparer.Ordinal);
        foreach ((string key, object value) in values)
        {
            string raw = value switch
            {
                string s => $"\"{s}\"",
                bool b => b ? "true" : "false",
                int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => throw new ArgumentException($"Unsupported option value: {value}")
            };
            dict[key] = JsonDocument.Parse(raw).RootElement.Clone();
        }

        return dict;
    }

    [Fact]
    public async Task WhenNoOptionsThenDefaultExactMatchAppliesEdit()
    {
        string path = Path.Combine(_tempDir, "sample.txt");
        await File.WriteAllTextAsync(path, "line one\nline two\nline three\n");

        ToolCallResult result = await ExecuteEditAsync("sample.txt", "line two", "LINE TWO");

        Assert.True(result.Success, result.Error);
        Assert.Equal("line one\nLINE TWO\nline three\n", await ReadNormalizedAsync(path));
    }

    [Fact]
    public async Task WhenExactMatchIsAmbiguousThenFailsByDefault()
    {
        string path = Path.Combine(_tempDir, "dup.txt");
        await File.WriteAllTextAsync(path, "same\nother\nsame\n");

        ToolCallResult result = await ExecuteEditAsync("dup.txt", "same", "changed");

        Assert.False(result.Success);
        Assert.Contains("2 locations", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenOnMultipleMatchesIsFirstOnlyThenFirstMatchWins()
    {
        string path = Path.Combine(_tempDir, "dup.txt");
        await File.WriteAllTextAsync(path, "same\nother\nsame\n");

        ToolCallResult result = await ExecuteEditAsync(
            "dup.txt",
            "same",
            "changed",
            Options(("on_multiple_matches", "first_only")));

        Assert.True(result.Success, result.Error);
        Assert.Equal("changed\nother\nsame\n", await ReadNormalizedAsync(path));
    }

    [Fact]
    public async Task WhenCaseSensitiveIsTrueThenCaseMismatchFails()
    {
        string path = Path.Combine(_tempDir, "case.txt");
        await File.WriteAllTextAsync(path, "Hello World\n");

        ToolCallResult result = await ExecuteEditAsync(
            "case.txt",
            "hello world",
            "HELLO WORLD",
            Options(("case_sensitive", true), ("engine", "exact_match")));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task WhenAnchoredReplaceUsedThenIndentationDriftIsTolerated()
    {
        string path = Path.Combine(_tempDir, "anchored.txt");
        await File.WriteAllTextAsync(path, "namespace X\n{\n    public class Foo\n    {\n    }\n}\n");

        // oldText is indented with 4 spaces; file uses 4 spaces too but let's
        // simulate drift by using a differently-indented block.
        ToolCallResult result = await ExecuteEditAsync(
            "anchored.txt",
            "public class Foo\n    {",
            "public class Bar\n    {",
            Options(("engine", "anchored_replace")));

        Assert.True(result.Success, result.Error);
        Assert.Contains("public class Bar", await File.ReadAllTextAsync(path));    }

    [Fact]
    public async Task WhenUnifiedDiffUsedThenContextLineToleranceApplies()
    {
        string path = Path.Combine(_tempDir, "unified.txt");
        await File.WriteAllTextAsync(path, "alpha\nbeta\ngamma\ndelta\nepsilon\n");

        // oldText has one line that differs from the file ("BETA" vs "beta")
        // but context_lines = 3 tolerates it.
        ToolCallResult result = await ExecuteEditAsync(
            "unified.txt",
            "alpha\nBETA\ngamma",
            "ALPHA\nbeta\nGAMMA",
            Options(("engine", "unified_diff"), ("context_lines", 3)));

        Assert.True(result.Success, result.Error);
        Assert.Equal("ALPHA\nbeta\nGAMMA\ndelta\nepsilon\n", await ReadNormalizedAsync(path));
    }

    [Fact]
    public async Task WhenUnifiedDiffHasTooManyDifferencesThenFails()
    {
        string path = Path.Combine(_tempDir, "unified2.txt");
        await File.WriteAllTextAsync(path, "one\ntwo\nthree\nfour\n");

        ToolCallResult result = await ExecuteEditAsync(
            "unified2.txt",
            "one\ntwo\nFOUR",
            "1\ntwo\n4",
            Options(("engine", "unified_diff"), ("context_lines", 0)));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task WhenPathScopeIsProjectThenExternalPathIsBlocked()
    {
        string outsideDir = Path.Combine(Path.GetTempPath(), $"KaneCodeOutside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);
        string outsideFile = Path.Combine(outsideDir, "outside.txt");
        await File.WriteAllTextAsync(outsideFile, "content");

        try
        {
            string escaped = outsideFile.Replace("\\", "\\\\", StringComparison.Ordinal);
            JsonElement args = JsonDocument.Parse($"{{\"filePath\":\"{escaped}\",\"oldText\":\"content\",\"newText\":\"hacked\"}}").RootElement;
            using (AgentToolContext.Push(Options(("path_scope", "project"))))
            {
                ToolCallResult result = await _tool.ExecuteAsync(args);
                Assert.False(result.Success);
                Assert.Contains("inside the loaded project", result.Error, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            try { Directory.Delete(outsideDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task WhenDefaultOptionsThenBackendOptionsSchemaAndDefaultsAreExposed()
    {
        Assert.NotEqual(JsonValueKind.Undefined, _tool.BackendOptionsSchema.ValueKind);

        IReadOnlyDictionary<string, JsonElement> defaults = _tool.DefaultBackendOptions;

        Assert.Equal("\"exact_match\"", defaults["engine"].GetRawText());
        Assert.Equal("30", defaults["timeout"].GetRawText());
        Assert.Equal("true", defaults["require_confirmation"].GetRawText());
    }

    [Fact]
    public async Task WhenEditSucceedsThenDetailsContainPathLineRangeAndDiff()
    {
        string path = Path.Combine(_tempDir, "detail.txt");
        await File.WriteAllTextAsync(path, "line one\nline two\nline three\n");

        ToolCallResult result = await ExecuteEditAsync("detail.txt", "line two", "LINE TWO");

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Details);
        // Path is shown relative to the project root.
        Assert.Contains("detail.txt", result.Details, StringComparison.Ordinal);
        // The edit is at line 2.
        Assert.Contains("Line 2", result.Details, StringComparison.Ordinal);
        // The diff shows the removed and added lines.
        Assert.Contains("- line two", result.Details, StringComparison.Ordinal);
        Assert.Contains("+ LINE TWO", result.Details, StringComparison.Ordinal);
        // Model-facing output stays concise.
        Assert.StartsWith("Edit applied at line 2 in '", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenEditChangesLineCountThenDetailsReportRemovedAndAdded()
    {
        string path = Path.Combine(_tempDir, "multiline.txt");
        await File.WriteAllTextAsync(path, "alpha\nbeta\ngamma\ndelta\n");

        ToolCallResult result = await ExecuteEditAsync(
            "multiline.txt",
            "beta\ngamma",
            "BETA AND GAMMA");

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Details);
        // Two lines removed (lines 2-3), one line added.
        Assert.Contains("Lines 2-3", result.Details, StringComparison.Ordinal);
        Assert.Contains("Removed 2 line(s), added 1 line(s)", result.Details, StringComparison.Ordinal);
        Assert.Contains("- beta", result.Details, StringComparison.Ordinal);
        Assert.Contains("- gamma", result.Details, StringComparison.Ordinal);
        Assert.Contains("+ BETA AND GAMMA", result.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenEditSucceedsThenDetailsDoNotConsumeModelOutput()
    {
        string path = Path.Combine(_tempDir, "output.txt");
        await File.WriteAllTextAsync(path, "same\n");

        ToolCallResult result = await ExecuteEditAsync("output.txt", "same", "changed");

        Assert.True(result.Success, result.Error);
        // Output stays the concise, model-bound text; the rich detail is separate.
        Assert.Equal("Edit applied at line 1 in '" + Path.Combine(_tempDir, "output.txt") + "'.", result.Output);
        Assert.NotEqual(result.Output, result.Details);
    }
}
